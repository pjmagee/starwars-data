using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Pure C# executor (no LLM) that queries MongoDB to discover all pages
/// relevant to a character via infobox links AND knowledge graph edges.
/// Loads article chunk content (when available) for richer extraction.
/// Stores page content in shared workflow state for downstream executors.
/// </summary>
internal sealed class PageDiscoveryExecutor : Executor<string, string>
{
    private readonly IMongoClient _mongoClient;
    private readonly SettingsOptions _settings;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;

    private const int MaxContentChars = 4000;
    private const int MaxChunkContentChars = 8000;
    private const int MaxLinkedResults = 20;
    private const int MaxKgResults = 40;

    private const string StateKeySources = "discoveredSources";
    private const string StateKeyCharacter = "characterSnapshot";
    private const string StateScope = "Discovery";

    /// <summary>
    /// Pages discovered during execution — used to record sources.
    /// Mirrored into workflow state in <see cref="OnCheckpointingAsync"/> so it survives resume.
    /// </summary>
    public List<SourcePage> DiscoveredSources { get; private set; } = [];

    /// <summary>
    /// Minimal snapshot of the character page, used post-workflow to build the final timeline doc.
    /// Mirrored into workflow state so it survives resume.
    /// </summary>
    public CharacterSnapshot? Character { get; private set; }

    public PageDiscoveryExecutor(IMongoClient mongoClient, SettingsOptions settings, ILogger logger, CharacterTimelineTracker? tracker)
        : base("PageDiscovery")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _tracker = tracker;
    }

    private IMongoCollection<Page> Pages => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<Page>(Collections.Pages);

    private IMongoCollection<RelationshipEdge> KgEdges => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    private IMongoCollection<GraphNode> KgNodes => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    private IMongoCollection<ArticleChunk> Chunks => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<ArticleChunk>(Collections.SearchChunks);

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Mirror instance state into the checkpoint so a resumed run can rehydrate without re-running discovery.
        await context.QueueStateUpdateAsync(StateKeySources, DiscoveredSources, StateScope, cancellationToken);
        if (Character is not null)
            await context.QueueStateUpdateAsync(StateKeyCharacter, Character, StateScope, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sources = await context.ReadStateAsync<List<SourcePage>>(StateKeySources, StateScope, cancellationToken);
        if (sources is not null)
            DiscoveredSources = sources;

        var character = await context.ReadStateAsync<CharacterSnapshot>(StateKeyCharacter, StateScope, cancellationToken);
        if (character is not null)
            Character = character;

        _logger.LogInformation("Restored discovery state: character={Character}, sources={SourceCount}", Character?.Title ?? "(null)", DiscoveredSources.Count);
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var pageId = int.Parse(message);

        // Load character page
        var character = await Pages.Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId)).FirstOrDefaultAsync(ct);

        if (character is null)
            return "Character not found";

        Character = new CharacterSnapshot(character.PageId, character.Title, character.WikiUrl, character.Infobox?.ImageUrl, character.Continuity);

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering, $"Querying pages that link to {character.Title}...", currentStep: 1, totalSteps: 4, currentItem: character.Title);

        _logger.LogInformation("Discovering pages for {Title} (PageId={PageId})", character.Title, pageId);

        var discoveredPages = new Dictionary<int, Page> { [character.PageId] = character };
        var allowedContinuities = GetAllowedContinuities(character.Continuity);
        var continuityFilter = Builders<Page>.Filter.In(p => p.Continuity, allowedContinuities);

        // Emit event for the character's own page
        await context.AddEventAsync(
            new PageDiscoveredEvent(new PageDiscoveredData(character.PageId, character.Title, character.WikiUrl, character.Infobox?.Template, character.Continuity.ToString(), "self")),
            ct
        );

        // ── Phase 1: Incoming infobox links (pages that link TO this character) ──
        var incomingLinkFilter = new BsonDocument(
            "infobox.Data",
            new BsonDocument(
                "$elemMatch",
                new BsonDocument("Links", new BsonDocument("$elemMatch", new BsonDocument("Href", new BsonDocument("$regex", MongoSafe.Regex(character.WikiUrl, escape: true)))))
            )
        );

        var incomingPages = await Pages.Find(Builders<Page>.Filter.And(new BsonDocumentFilterDefinition<Page>(incomingLinkFilter), continuityFilter)).Limit(MaxLinkedResults).ToListAsync(ct);

        var incomingCount = 0;
        foreach (var p in incomingPages)
        {
            if (discoveredPages.TryAdd(p.PageId, p))
            {
                incomingCount++;
                await context.AddEventAsync(new PageDiscoveredEvent(new PageDiscoveredData(p.PageId, p.Title, p.WikiUrl, p.Infobox?.Template, p.Continuity.ToString(), "incoming")), ct);
            }
        }

        _logger.LogInformation("Found {Count} incoming-linked pages for {Title}", incomingCount, character.Title);

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering, $"Found {incomingCount} incoming links. Querying outgoing links...", currentStep: 2, totalSteps: 4, currentItem: character.Title);

        // ── Phase 2: Outgoing infobox links (pages the character's infobox links to) ──
        var outgoingCount = 0;
        if (character.Infobox?.Data is not null)
        {
            var urls = character.Infobox.Data.SelectMany(d => d.Links).Select(l => l.Href).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();

            if (urls.Count > 0)
            {
                var outgoingPages = await Pages.Find(Builders<Page>.Filter.And(Builders<Page>.Filter.In(p => p.WikiUrl, urls), continuityFilter)).Limit(MaxLinkedResults).ToListAsync(ct);

                foreach (var p in outgoingPages)
                {
                    if (discoveredPages.TryAdd(p.PageId, p))
                    {
                        outgoingCount++;
                        await context.AddEventAsync(new PageDiscoveredEvent(new PageDiscoveredData(p.PageId, p.Title, p.WikiUrl, p.Infobox?.Template, p.Continuity.ToString(), "outgoing")), ct);
                    }
                }

                _logger.LogInformation("Found {Count} outgoing-linked pages for {Title}", outgoingCount, character.Title);
            }
        }

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Found {incomingCount} incoming + {outgoingCount} outgoing links. Querying knowledge graph...",
            currentStep: 3,
            totalSteps: 4,
            currentItem: character.Title
        );

        // ── Phase 3: Knowledge graph edges ──
        var kgCount = 0;
        var kgEdges = await KgEdges
            .Find(Builders<RelationshipEdge>.Filter.And(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, pageId), Builders<RelationshipEdge>.Filter.In(e => e.Continuity, allowedContinuities)))
            .SortByDescending(e => e.Weight)
            .Limit(MaxKgResults)
            .ToListAsync(ct);

        var kgTargetIds = kgEdges.Select(e => e.ToId).Where(id => id != pageId && !discoveredPages.ContainsKey(id)).Distinct().ToList();

        if (kgTargetIds.Count > 0)
        {
            var kgPages = await Pages.Find(Builders<Page>.Filter.In(p => p.PageId, kgTargetIds)).ToListAsync(ct);

            foreach (var p in kgPages)
            {
                if (discoveredPages.TryAdd(p.PageId, p))
                {
                    kgCount++;
                    await context.AddEventAsync(new PageDiscoveredEvent(new PageDiscoveredData(p.PageId, p.Title, p.WikiUrl, p.Infobox?.Template, p.Continuity.ToString(), "knowledge-graph")), ct);
                }
            }
        }

        _logger.LogInformation("Found {KgEdgeCount} KG edges -> {KgPageCount} new pages for {Title}", kgEdges.Count, kgCount, character.Title);

        // ── Load article chunks for richer content ──
        var allPageIds = discoveredPages.Keys.ToList();
        var chunks = await Chunks.Find(Builders<ArticleChunk>.Filter.In(c => c.PageId, allPageIds)).SortBy(c => c.PageId).ThenBy(c => c.ChunkIndex).ToListAsync(ct);

        var chunksByPageId = chunks.GroupBy(c => c.PageId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.ChunkIndex).ToList());

        // ── Build page content for downstream extraction ──
        var pageContents = new List<PageContent>();
        foreach (var page in discoveredPages.Values)
        {
            string contentSnippet;

            if (chunksByPageId.TryGetValue(page.PageId, out var pageChunks) && pageChunks.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var chunk in pageChunks)
                {
                    if (!string.IsNullOrWhiteSpace(chunk.Heading))
                        sb.AppendLine($"## {chunk.Heading}");
                    sb.AppendLine(chunk.Text);
                    sb.AppendLine();
                }
                contentSnippet = Truncate(sb.ToString(), MaxChunkContentChars);
            }
            else
            {
                contentSnippet = Truncate(page.Content ?? "", MaxContentChars);
            }

            // Include KG relationship context if edges connect the character to this page
            string? kgContext = null;
            var edgesToPage = kgEdges.Where(e => e.ToId == page.PageId).ToList();
            if (edgesToPage.Count > 0)
            {
                kgContext = string.Join("\n", edgesToPage.Select(e => $"- {character.Title} -> {e.Label} -> {page.Title}" + (string.IsNullOrEmpty(e.Evidence) ? "" : $" ({e.Evidence})")));
            }

            pageContents.Add(new PageContent(page.PageId, page.Title, page.WikiUrl, page.Infobox?.Template, FormatInfobox(page), contentSnippet, kgContext));

            DiscoveredSources.Add(
                new SourcePage
                {
                    PageId = page.PageId,
                    Title = page.Title,
                    WikiUrl = page.WikiUrl,
                }
            );
        }

        // ── Phase 4: KG temporal pre-pass ──
        // Build deterministic temporal anchors (ground-truth dates the LLM must honour) and
        // lifespan bounds (hallucination filter) from the character's GraphNode and its KG edges.
        // This is the single biggest improvement to timeline quality — the LLM stops guessing years.
        var (anchors, lifespan) = await BuildTemporalAnchorsAsync(pageId, character, kgEdges, discoveredPages, ct);

        _logger.LogInformation(
            "Built {AnchorCount} KG temporal anchors for {Title} (lifespan: {Start} → {End})",
            anchors.Count,
            character.Title,
            lifespan.StartYear?.ToString() ?? "?",
            lifespan.EndYear?.ToString() ?? "?"
        );

        // Store in shared state
        await context.QueueStateUpdateAsync("pages", pageContents, StateScope, ct);
        await context.QueueStateUpdateAsync("characterTitle", character.Title, StateScope, ct);
        await context.QueueStateUpdateAsync("characterContinuity", character.Continuity.ToString(), StateScope, ct);
        await context.QueueStateUpdateAsync("anchors", anchors, StateScope, ct);
        await context.QueueStateUpdateAsync("lifespan", lifespan, StateScope, ct);

        await context.AddEventAsync(new DiscoveryCompleteEvent(new DiscoveryCompleteData(pageContents.Count, incomingCount, outgoingCount, kgCount)), ct);

        _logger.LogInformation(
            "Stored {Count} pages in workflow state for {Title} ({Incoming} incoming, {Outgoing} outgoing, {Kg} KG)",
            pageContents.Count,
            character.Title,
            incomingCount,
            outgoingCount,
            kgCount
        );

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering, $"Discovered {pageContents.Count} pages for {character.Title}", currentStep: 4, totalSteps: 4, currentItem: character.Title);

        return $"Discovered {pageContents.Count} pages for {character.Title}";
    }

    /// <summary>
    /// Build deterministic temporal anchors from the character's knowledge graph neighbourhood.
    /// These are passed to the extraction LLM as ground truth — the LLM must honour them rather
    /// than guessing years from prose. Also derives lifespan bounds used as a hallucination filter.
    /// </summary>
    private async Task<(List<TemporalAnchor> Anchors, LifespanBounds Lifespan)> BuildTemporalAnchorsAsync(
        int characterPageId,
        Page characterPage,
        List<RelationshipEdge> characterEdges,
        Dictionary<int, Page> discoveredPages,
        CancellationToken ct
    )
    {
        var anchors = new List<TemporalAnchor>();

        // ── Character's own GraphNode — lifespan + other temporal facets ──
        var characterNode = await KgNodes.Find(Builders<GraphNode>.Filter.Eq(n => n.PageId, characterPageId)).FirstOrDefaultAsync(ct);

        var lifespan = new LifespanBounds(characterNode?.StartYear, characterNode?.EndYear);

        if (characterNode is not null)
        {
            foreach (var facet in characterNode.TemporalFacets.Where(f => f.Year.HasValue && f.Calendar == "galactic"))
            {
                var (eventType, prefix) = facet.Semantic switch
                {
                    "lifespan.start" => ("Birth", $"{characterPage.Title} was born"),
                    "lifespan.end" => ("Death", $"{characterPage.Title} died"),
                    _ => (string.Empty, string.Empty),
                };
                if (eventType.Length == 0)
                    continue;

                var (year, demarcation) = SplitGalacticYear(facet.Year!.Value);
                anchors.Add(
                    new TemporalAnchor(
                        EventType: eventType,
                        Description: $"{prefix}{(string.IsNullOrWhiteSpace(facet.Text) ? "" : $" ({facet.Text})")}",
                        Year: year,
                        Demarcation: demarcation,
                        Location: null,
                        RelatedCharacters: [],
                        SourcePageTitle: characterPage.Title,
                        SourceWikiUrl: characterPage.WikiUrl
                    )
                );
            }
        }

        // ── KG edges with temporal bounds → relationship anchors ──
        // Load target GraphNodes so we can fall back to target.StartYear when the edge itself has no FromYear.
        var targetIds = characterEdges.Select(e => e.ToId).Distinct().ToList();
        var targetNodes = targetIds.Count > 0 ? (await KgNodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, targetIds)).ToListAsync(ct)).ToDictionary(n => n.PageId) : [];

        foreach (var edge in characterEdges)
        {
            // Use edge bounds first, fall back to the target node's lifespan (useful for Battle/War/Event targets)
            var year = edge.FromYear ?? (targetNodes.TryGetValue(edge.ToId, out var targetNode) ? targetNode.StartYear : null);
            if (year is null)
                continue;

            var (y, demarcation) = SplitGalacticYear(year.Value);
            var label = edge.Label.Replace('_', ' ');
            var targetName = edge.ToName;
            var eventType = InferEventTypeFromLabel(edge.Label);
            var endClause = edge.ToYear.HasValue && edge.ToYear != edge.FromYear ? $" until {FormatGalacticYear(edge.ToYear.Value)}" : "";

            // Prefer the target page's wiki URL as the source of this anchor (it's more specific than the character page)
            string sourceTitle = characterPage.Title;
            string? sourceUrl = characterPage.WikiUrl;
            if (discoveredPages.TryGetValue(edge.ToId, out var targetPage))
            {
                sourceTitle = targetPage.Title;
                sourceUrl = targetPage.WikiUrl;
            }

            anchors.Add(
                new TemporalAnchor(
                    EventType: eventType,
                    Description: $"{characterPage.Title} {label} {targetName}{endClause}".Trim(),
                    Year: y,
                    Demarcation: demarcation,
                    Location: null,
                    RelatedCharacters: [targetName],
                    SourcePageTitle: sourceTitle,
                    SourceWikiUrl: sourceUrl
                )
            );
        }

        return (anchors, lifespan);
    }

    /// <summary>
    /// Convert a galactic sort-key year into (magnitude, BBY/ABY). Negative = BBY, positive = ABY.
    /// </summary>
    private static (int Year, string Demarcation) SplitGalacticYear(int sortKey) => sortKey < 0 ? (-sortKey, "BBY") : (sortKey, "ABY");

    private static string FormatGalacticYear(int sortKey)
    {
        var (year, demarcation) = SplitGalacticYear(sortKey);
        return $"{year} {demarcation}";
    }

    /// <summary>
    /// Heuristic mapping from canonical edge labels to our event-type taxonomy.
    /// </summary>
    private static string InferEventTypeFromLabel(string label)
    {
        var l = label.ToLowerInvariant();
        return l switch
        {
            _ when l.Contains("born") => "Birth",
            _ when l.Contains("died") || l.Contains("killed") => "Death",
            _ when l.Contains("apprentice") => "Apprenticeship",
            _ when l.Contains("married") || l.Contains("spouse") => "Marriage",
            _ when l.Contains("battle") || l.Contains("fought") || l.Contains("participated") => "Battle",
            _ when l.Contains("founded") || l.Contains("established") || l.Contains("created") => "Founding",
            _ when l.Contains("destroyed") || l.Contains("destruction") => "Destruction",
            _ when l.Contains("member") || l.Contains("joined") || l.Contains("served") || l.Contains("employed") => "Alliance",
            _ when l.Contains("trained") || l.Contains("student") => "Training",
            _ when l.Contains("captured") => "Capture",
            _ when l.Contains("rescued") => "Rescue",
            _ when l.Contains("betrayed") => "Betrayal",
            _ when l.Contains("exiled") => "Exile",
            _ when l.Contains("promoted") || l.Contains("knighted") => "Promotion",
            _ => "Other",
        };
    }

    private static string FormatInfobox(Page page)
    {
        if (page.Infobox?.Data is null)
            return "";

        var sb = new StringBuilder();
        foreach (var prop in page.Infobox.Data)
        {
            var values = prop.Values.Count > 0 ? string.Join(", ", prop.Values) : "";
            var links = prop.Links.Count > 0 ? string.Join(", ", prop.Links.Select(l => $"{l.Content} [{l.Href}]")) : "";
            var display =
                !string.IsNullOrEmpty(values) ? values
                : !string.IsNullOrEmpty(links) ? links
                : null;
            if (display is not null)
                sb.AppendLine($"- {prop.Label}: {display}");
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int maxChars) => text.Length > maxChars ? text[..maxChars] + "…" : text;

    private static List<Continuity> GetAllowedContinuities(Continuity continuity)
    {
        var allowed = new List<Continuity> { Continuity.Both, Continuity.Unknown };

        if (continuity is Continuity.Canon or Continuity.Legends)
            allowed.Add(continuity);
        else
        {
            allowed.Add(Continuity.Canon);
            allowed.Add(Continuity.Legends);
        }

        return allowed;
    }
}

/// <summary>
/// Serializable page content stored in workflow state.
/// </summary>
internal sealed record PageContent(int PageId, string Title, string WikiUrl, string? Template, string InfoboxText, string ContentSnippet, string? KgContext = null);

/// <summary>
/// Minimal serializable snapshot of the character page, kept in workflow state so the
/// final timeline doc can be built even when a run was resumed from a checkpoint
/// (in which case PageDiscoveryExecutor.HandleAsync is never re-entered).
/// </summary>
internal sealed record CharacterSnapshot(int PageId, string Title, string WikiUrl, string? ImageUrl, Continuity Continuity);

/// <summary>
/// A deterministic temporal fact about the character, derived from the knowledge graph
/// (the character's GraphNode temporal facets and KG edges with time bounds).
/// Passed into the extraction prompt so the LLM can anchor vague events to real dates
/// and so obvious events (birth/death/joined X/fought at Y) are guaranteed to appear.
/// </summary>
internal sealed record TemporalAnchor(
    string EventType,
    string Description,
    int? Year,
    string? Demarcation,
    string? Location,
    List<string> RelatedCharacters,
    string SourcePageTitle,
    string? SourceWikiUrl
);

/// <summary>
/// Derived lifespan bounds for the character, used to filter out events that fall
/// outside the plausible window (cheap hallucination filter).
/// </summary>
internal sealed record LifespanBounds(int? StartYear, int? EndYear);
