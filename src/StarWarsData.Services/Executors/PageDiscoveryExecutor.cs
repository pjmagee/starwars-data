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
/// relevant to a character. Stores page content in shared workflow state
/// for downstream executors to process one at a time.
/// </summary>
internal sealed class PageDiscoveryExecutor : Executor<string, string>
{
    private readonly IMongoClient _mongoClient;
    private readonly SettingsOptions _settings;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;

    private const int MaxContentChars = 4000;
    private const int MaxLinkedResults = 20;

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
            totalSteps: 3,
            currentItem: character.Title
        );

        _logger.LogInformation(
            "Discovering pages for {Title} (PageId={PageId})",
            character.Title,
            pageId
        );

        var discoveredPages = new Dictionary<int, Page> { [character.PageId] = character };

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

        // Build a continuity filter so we only discover pages matching the character's continuity.
        // Include pages marked as Both or Unknown alongside the character's specific continuity.
        var continuityFilter = BuildContinuityFilter(character.Continuity);

        // 1. Find pages that link TO this character (battles, events, etc.)
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

        var incomingFilter = Builders<Page>.Filter.And(
            new BsonDocumentFilterDefinition<Page>(incomingLinkFilter),
            continuityFilter
        );

        var incomingPages = await Pages
            .Find(incomingFilter)
            .Limit(MaxLinkedResults)
            .ToListAsync(ct);

        foreach (var p in incomingPages)
        {
            if (discoveredPages.TryAdd(p.PageId, p))
            {
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
            "Found {Count} pages linking to {Title}",
            incomingPages.Count,
            character.Title
        );

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Found {incomingPages.Count} incoming links. Querying outgoing links...",
            currentStep: 2,
            totalSteps: 3,
            currentItem: character.Title
        );

        // 2. Find pages the character's infobox links to (outgoing links)
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
                    outgoingPages.Count,
                    character.Title
                );
            }
        }

        // Store each page's content in state for per-page extraction
        var pageContents = new List<PageContent>();
        foreach (var page in discoveredPages.Values)
        {
            pageContents.Add(
                new PageContent(
                    page.PageId,
                    page.Title,
                    page.WikiUrl,
                    page.Infobox?.Template,
                    FormatInfobox(page),
                    Truncate(page.Content ?? "", MaxContentChars)
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

        // Store in shared state as a single list
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
                new DiscoveryCompleteData(
                    pageContents.Count,
                    incomingPages.Count,
                    discoveredPages.Count - 1 - incomingPages.Count
                )
            ),
            ct
        );

        _logger.LogInformation(
            "Stored {Count} pages in workflow state for {Title}",
            pageContents.Count,
            character.Title
        );

        _tracker?.UpdateProgress(
            pageId,
            GenerationStage.Discovering,
            $"Discovered {pageContents.Count} pages for {character.Title}",
            currentStep: 3,
            totalSteps: 3,
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

    /// <summary>
    /// Build a MongoDB filter to restrict discovered pages to the character's continuity.
    /// Includes pages marked as Both or Unknown alongside the character's specific continuity.
    /// </summary>
    private static FilterDefinition<Page> BuildContinuityFilter(Continuity continuity)
    {
        // Always include pages marked Both or Unknown
        var allowed = new List<Continuity> { Continuity.Both, Continuity.Unknown };

        if (continuity is Continuity.Canon or Continuity.Legends)
            allowed.Add(continuity);
        else
        {
            // If the character itself is Both or Unknown, include everything
            allowed.Add(Continuity.Canon);
            allowed.Add(Continuity.Legends);
        }

        return Builders<Page>.Filter.In(p => p.Continuity, allowed);
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
    string ContentSnippet
);
