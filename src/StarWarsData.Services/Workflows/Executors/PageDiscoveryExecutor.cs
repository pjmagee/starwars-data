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

    /// <summary>Pages discovered during execution — used to record sources.</summary>
    public List<SourcePage> DiscoveredSources { get; } = [];

    /// <summary>Character page loaded during execution.</summary>
    public Page? Character { get; private set; }

    public PageDiscoveryExecutor(
        IMongoClient mongoClient,
        SettingsOptions settings,
        ILogger logger,
        CharacterTimelineTracker? tracker
    )
        : base("PageDiscovery")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _tracker = tracker;
    }

    private IMongoCollection<Page> Pages =>
        _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<Page>(Collections.Pages);

    private IMongoCollection<RelationshipEdge> KgEdges =>
        _mongoClient
            .GetDatabase(_settings.DatabaseName)
            .GetCollection<RelationshipEdge>(Collections.KgEdges);

    private IMongoCollection<ArticleChunk> Chunks =>
        _mongoClient
            .GetDatabase(_settings.DatabaseName)
            .GetCollection<ArticleChunk>(Collections.SearchChunks);

    public override async ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken ct = default
    )
    {
        var pageId = int.Parse(message);

        // Load character page
        var character = await Pages
            .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId))
            .FirstOrDefaultAsync(ct);

        if (character is null)
            return "Character not found";

        Character = character;

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Querying pages that link to {character.Title}...",
            currentStep: 1,
            totalSteps: 4,
            currentItem: character.Title
        );

        _logger.LogInformation(
            "Discovering pages for {Title} (PageId={PageId})",
            character.Title,
            pageId
        );

        var discoveredPages = new Dictionary<int, Page> { [character.PageId] = character };
        var allowedContinuities = GetAllowedContinuities(character.Continuity);
        var continuityFilter = Builders<Page>.Filter.In(p => p.Continuity, allowedContinuities);

        // Emit event for the character's own page
        await context.AddEventAsync(
            new PageDiscoveredEvent(
                new PageDiscoveredData(
                    character.PageId,
                    character.Title,
                    character.WikiUrl,
                    character.Infobox?.Template,
                    character.Continuity.ToString(),
                    "self"
                )
            ),
            ct
        );

        // ── Phase 1: Incoming infobox links (pages that link TO this character) ──
        var incomingLinkFilter = new BsonDocument(
            "infobox.Data",
            new BsonDocument(
                "$elemMatch",
                new BsonDocument(
                    "Links",
                    new BsonDocument(
                        "$elemMatch",
                        new BsonDocument(
                            "Href",
                            new BsonDocument(
                                "$regex",
                                new BsonRegularExpression(Regex.Escape(character.WikiUrl), "i")
                            )
                        )
                    )
                )
            )
        );

        var incomingPages = await Pages
            .Find(
                Builders<Page>.Filter.And(
                    new BsonDocumentFilterDefinition<Page>(incomingLinkFilter),
                    continuityFilter
                )
            )
            .Limit(MaxLinkedResults)
            .ToListAsync(ct);

        var incomingCount = 0;
        foreach (var p in incomingPages)
        {
            if (discoveredPages.TryAdd(p.PageId, p))
            {
                incomingCount++;
                await context.AddEventAsync(
                    new PageDiscoveredEvent(
                        new PageDiscoveredData(
                            p.PageId,
                            p.Title,
                            p.WikiUrl,
                            p.Infobox?.Template,
                            p.Continuity.ToString(),
                            "incoming"
                        )
                    ),
                    ct
                );
            }
        }

        _logger.LogInformation(
            "Found {Count} incoming-linked pages for {Title}",
            incomingCount,
            character.Title
        );

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Found {incomingCount} incoming links. Querying outgoing links...",
            currentStep: 2,
            totalSteps: 4,
            currentItem: character.Title
        );

        // ── Phase 2: Outgoing infobox links (pages the character's infobox links to) ──
        var outgoingCount = 0;
        if (character.Infobox?.Data is not null)
        {
            var urls = character
                .Infobox.Data.SelectMany(d => d.Links)
                .Select(l => l.Href)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct()
                .ToList();

            if (urls.Count > 0)
            {
                var outgoingPages = await Pages
                    .Find(
                        Builders<Page>.Filter.And(
                            Builders<Page>.Filter.In(p => p.WikiUrl, urls),
                            continuityFilter
                        )
                    )
                    .Limit(MaxLinkedResults)
                    .ToListAsync(ct);

                foreach (var p in outgoingPages)
                {
                    if (discoveredPages.TryAdd(p.PageId, p))
                    {
                        outgoingCount++;
                        await context.AddEventAsync(
                            new PageDiscoveredEvent(
                                new PageDiscoveredData(
                                    p.PageId,
                                    p.Title,
                                    p.WikiUrl,
                                    p.Infobox?.Template,
                                    p.Continuity.ToString(),
                                    "outgoing"
                                )
                            ),
                            ct
                        );
                    }
                }

                _logger.LogInformation(
                    "Found {Count} outgoing-linked pages for {Title}",
                    outgoingCount,
                    character.Title
                );
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
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, pageId),
                    Builders<RelationshipEdge>.Filter.In(e => e.Continuity, allowedContinuities)
                )
            )
            .SortByDescending(e => e.Weight)
            .Limit(MaxKgResults)
            .ToListAsync(ct);

        var kgTargetIds = kgEdges
            .Select(e => e.ToId)
            .Where(id => id != pageId && !discoveredPages.ContainsKey(id))
            .Distinct()
            .ToList();

        if (kgTargetIds.Count > 0)
        {
            var kgPages = await Pages
                .Find(Builders<Page>.Filter.In(p => p.PageId, kgTargetIds))
                .ToListAsync(ct);

            foreach (var p in kgPages)
            {
                if (discoveredPages.TryAdd(p.PageId, p))
                {
                    kgCount++;
                    await context.AddEventAsync(
                        new PageDiscoveredEvent(
                            new PageDiscoveredData(
                                p.PageId,
                                p.Title,
                                p.WikiUrl,
                                p.Infobox?.Template,
                                p.Continuity.ToString(),
                                "knowledge-graph"
                            )
                        ),
                        ct
                    );
                }
            }
        }

        _logger.LogInformation(
            "Found {KgEdgeCount} KG edges -> {KgPageCount} new pages for {Title}",
            kgEdges.Count,
            kgCount,
            character.Title
        );

        // ── Load article chunks for richer content ──
        var allPageIds = discoveredPages.Keys.ToList();
        var chunks = await Chunks
            .Find(Builders<ArticleChunk>.Filter.In(c => c.PageId, allPageIds))
            .SortBy(c => c.PageId)
            .ThenBy(c => c.ChunkIndex)
            .ToListAsync(ct);

        var chunksByPageId = chunks
            .GroupBy(c => c.PageId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ChunkIndex).ToList());

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
                kgContext = string.Join(
                    "\n",
                    edgesToPage.Select(e =>
                        $"- {character.Title} -> {e.Label} -> {page.Title}"
                        + (string.IsNullOrEmpty(e.Evidence) ? "" : $" ({e.Evidence})")
                    )
                );
            }

            pageContents.Add(
                new PageContent(
                    page.PageId,
                    page.Title,
                    page.WikiUrl,
                    page.Infobox?.Template,
                    FormatInfobox(page),
                    contentSnippet,
                    kgContext
                )
            );

            DiscoveredSources.Add(
                new SourcePage
                {
                    PageId = page.PageId,
                    Title = page.Title,
                    WikiUrl = page.WikiUrl,
                }
            );
        }

        // Store in shared state
        await context.QueueStateUpdateAsync("pages", pageContents, "Discovery", ct);
        await context.QueueStateUpdateAsync("characterTitle", character.Title, "Discovery", ct);
        await context.QueueStateUpdateAsync(
            "characterContinuity",
            character.Continuity.ToString(),
            "Discovery",
            ct
        );

        await context.AddEventAsync(
            new DiscoveryCompleteEvent(
                new DiscoveryCompleteData(pageContents.Count, incomingCount, outgoingCount, kgCount)
            ),
            ct
        );

        _logger.LogInformation(
            "Stored {Count} pages in workflow state for {Title} ({Incoming} incoming, {Outgoing} outgoing, {Kg} KG)",
            pageContents.Count,
            character.Title,
            incomingCount,
            outgoingCount,
            kgCount
        );

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Discovered {pageContents.Count} pages for {character.Title}",
            currentStep: 4,
            totalSteps: 4,
            currentItem: character.Title
        );

        return $"Discovered {pageContents.Count} pages for {character.Title}";
    }

    private static string FormatInfobox(Page page)
    {
        if (page.Infobox?.Data is null)
            return "";

        var sb = new StringBuilder();
        foreach (var prop in page.Infobox.Data)
        {
            var values = prop.Values.Count > 0 ? string.Join(", ", prop.Values) : "";
            var links =
                prop.Links.Count > 0
                    ? string.Join(", ", prop.Links.Select(l => $"{l.Content} [{l.Href}]"))
                    : "";
            var display =
                !string.IsNullOrEmpty(values) ? values
                : !string.IsNullOrEmpty(links) ? links
                : null;
            if (display is not null)
                sb.AppendLine($"- {prop.Label}: {display}");
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "…" : text;

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
internal sealed record PageContent(
    int PageId,
    string Title,
    string WikiUrl,
    string? Template,
    string InfoboxText,
    string ContentSnippet,
    string? KgContext = null
);
