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
        CharacterTimelineTracker? tracker)
        : base("PageDiscovery")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _tracker = tracker;
    }

    private IMongoCollection<Page> Pages =>
        _mongoClient.GetDatabase(_settings.PagesDb).GetCollection<Page>("Pages");

    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var pageId = int.Parse(message);

        // Load character page
        var character = await Pages
            .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId))
            .FirstOrDefaultAsync(ct);

        if (character is null)
            return "Character not found";

        Character = character;

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering,
            $"Querying pages that link to {character.Title}...",
            currentStep: 1, totalSteps: 3, currentItem: character.Title);

        _logger.LogInformation("Discovering pages for {Title} (PageId={PageId})", character.Title, pageId);

        var discoveredPages = new Dictionary<int, Page> { [character.PageId] = character };

        // 1. Find pages that link TO this character (battles, events, etc.)
        var incomingFilter = new BsonDocument("infobox.Data",
            new BsonDocument("$elemMatch",
                new BsonDocument("Links",
                    new BsonDocument("$elemMatch",
                        new BsonDocument("Href",
                            new BsonDocument("$regex",
                                new BsonRegularExpression(
                                    Regex.Escape(character.WikiUrl), "i")))))));

        var incomingPages = await Pages
            .Find(new BsonDocumentFilterDefinition<Page>(incomingFilter))
            .Limit(MaxLinkedResults)
            .ToListAsync(ct);

        foreach (var p in incomingPages)
            discoveredPages.TryAdd(p.PageId, p);

        _logger.LogInformation("Found {Count} pages linking to {Title}", incomingPages.Count, character.Title);

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering,
            $"Found {incomingPages.Count} incoming links. Querying outgoing links...",
            currentStep: 2, totalSteps: 3, currentItem: character.Title);

        // 2. Find pages the character's infobox links to (outgoing links)
        if (character.Infobox?.Data is not null)
        {
            var urls = character.Infobox.Data
                .SelectMany(d => d.Links)
                .Select(l => l.Href)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct()
                .ToList();

            if (urls.Count > 0)
            {
                var outgoingPages = await Pages
                    .Find(Builders<Page>.Filter.In(p => p.WikiUrl, urls))
                    .Limit(MaxLinkedResults)
                    .ToListAsync(ct);

                foreach (var p in outgoingPages)
                    discoveredPages.TryAdd(p.PageId, p);

                _logger.LogInformation("Found {Count} outgoing-linked pages for {Title}",
                    outgoingPages.Count, character.Title);
            }
        }

        // Store each page's content in state for per-page extraction
        var pageContents = new List<PageContent>();
        foreach (var page in discoveredPages.Values)
        {
            pageContents.Add(new PageContent(
                page.PageId,
                page.Title,
                page.WikiUrl,
                page.Infobox?.Template,
                FormatInfobox(page),
                Truncate(page.Content ?? "", MaxContentChars)));

            DiscoveredSources.Add(new SourcePage
            {
                PageId = page.PageId,
                Title = page.Title,
                WikiUrl = page.WikiUrl,
            });
        }

        // Store in shared state as a single list
        await context.QueueStateUpdateAsync("pages", pageContents, "Discovery", ct);
        await context.QueueStateUpdateAsync("characterTitle", character.Title, "Discovery", ct);

        _logger.LogInformation("Stored {Count} pages in workflow state for {Title}",
            pageContents.Count, character.Title);

        _tracker?.UpdateProgress(pageId, GenerationStage.Discovering,
            $"Discovered {pageContents.Count} pages for {character.Title}",
            currentStep: 3, totalSteps: 3, currentItem: character.Title);

        return $"Discovered {pageContents.Count} pages for {character.Title}";
    }

    private static string FormatInfobox(Page page)
    {
        if (page.Infobox?.Data is null) return "";

        var sb = new StringBuilder();
        foreach (var prop in page.Infobox.Data)
        {
            var values = prop.Values.Count > 0
                ? string.Join(", ", prop.Values)
                : "";
            var links = prop.Links.Count > 0
                ? string.Join(", ", prop.Links.Select(l => $"{l.Content} [{l.Href}]"))
                : "";
            var display = !string.IsNullOrEmpty(values) ? values
                : !string.IsNullOrEmpty(links) ? links
                : null;
            if (display is not null)
                sb.AppendLine($"- {prop.Label}: {display}");
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "…" : text;
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
    string ContentSnippet);
