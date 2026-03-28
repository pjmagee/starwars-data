using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Processes each discovered page individually with a small LLM call,
/// extracting timeline events. Accumulates all events in shared state.
/// Each page gets its own bounded context — no context window overflow.
///
/// Progress is saved to MongoDB after each page, so extraction can resume
/// mid-loop after an app restart — not just at superstep boundaries.
/// </summary>
internal sealed class EventExtractionExecutor : Executor<string, string>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;
    private readonly IMongoCollection<ExtractionProgressDoc> _progressCollection;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // In-memory state, seeded from MongoDB on HandleAsync entry
    private HashSet<int> _processedPageIds = [];
    private List<ExtractedEvent> _accumulatedEvents = [];

    public EventExtractionExecutor(
        IChatClient chatClient,
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId,
        IMongoClient mongoClient,
        string databaseName
    )
        : base("EventExtraction")
    {
        _chatClient = chatClient;
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
        _progressCollection = mongoClient
            .GetDatabase(databaseName)
            .GetCollection<ExtractionProgressDoc>("ExtractionProgress");
    }

    protected override ValueTask OnCheckpointingAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        // Still save to workflow state at superstep boundaries for framework consistency
        return context.QueueStateUpdateAsync(
            "checkpoint",
            new ExtractionCheckpoint(_processedPageIds.ToList(), _accumulatedEvents),
            "Extraction",
            cancellationToken
        );
    }

    protected override async ValueTask OnCheckpointRestoredAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        // Framework checkpoint restore — but our MongoDB progress is the real source of truth
        var checkpoint = await context.ReadStateAsync<ExtractionCheckpoint>(
            "checkpoint",
            "Extraction",
            cancellationToken
        );

        if (checkpoint is not null)
        {
            _processedPageIds = [.. checkpoint.ProcessedPageIds];
            _accumulatedEvents = [.. checkpoint.Events];
            _logger.LogInformation(
                "Restored extraction checkpoint from workflow state: {ProcessedCount} pages, {EventCount} events",
                _processedPageIds.Count,
                _accumulatedEvents.Count
            );
        }
    }

    public override async ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken ct = default
    )
    {
        var pages =
            await context.ReadStateAsync<List<PageContent>>("pages", "Discovery", ct)
            ?? throw new InvalidOperationException("No pages found in Discovery state");

        var characterTitle =
            await context.ReadStateAsync<string>("characterTitle", "Discovery", ct) ?? "Unknown";

        var characterContinuity =
            await context.ReadStateAsync<string>("characterContinuity", "Discovery", ct)
            ?? "Unknown";

        // ── Restore from MongoDB per-page progress (survives mid-extraction crashes) ──
        await RestoreFromMongoProgressAsync(ct);

        var remaining = pages.Where(p => !_processedPageIds.Contains(p.PageId)).ToList();

        if (_processedPageIds.Count > 0)
        {
            _logger.LogInformation(
                "Resuming extraction for {Title}: {Remaining}/{Total} pages remaining ({EventCount} events already accumulated)",
                characterTitle,
                remaining.Count,
                pages.Count,
                _accumulatedEvents.Count
            );
        }

        _tracker?.UpdateProgress(
            _characterPageId,
            GenerationStage.Extracting,
            $"Extracting from {remaining.Count} pages ({_processedPageIds.Count} already done)...",
            currentStep: _processedPageIds.Count,
            totalSteps: pages.Count,
            currentItem: remaining.FirstOrDefault()?.Title,
            eventsExtracted: _accumulatedEvents.Count
        );

        _logger.LogInformation(
            "Starting per-page extraction for {Title}: {Count} pages remaining",
            characterTitle,
            remaining.Count
        );

        var processed = _processedPageIds.Count;

        foreach (var page in remaining)
        {
            ct.ThrowIfCancellationRequested();

            _tracker?.UpdateProgress(
                _characterPageId,
                GenerationStage.Extracting,
                $"Extracting events from \"{page.Title}\" ({processed + 1}/{pages.Count})...",
                currentStep: processed,
                totalSteps: pages.Count,
                currentItem: page.Title,
                eventsExtracted: _accumulatedEvents.Count
            );

            await context.AddEventAsync(
                new ExtractionPageStartedEvent(
                    new ExtractionPageStartedData(page.Title, processed + 1, pages.Count)
                ),
                ct
            );

            try
            {
                var events = await ExtractEventsFromPage(
                    page,
                    characterTitle,
                    characterContinuity,
                    ct
                );
                _accumulatedEvents.AddRange(events);
                _processedPageIds.Add(page.PageId);
                processed++;

                // ── Save progress to MongoDB after each page ──
                await SaveMongoProgressAsync(ct);

                if (events.Count == 0)
                {
                    await context.AddEventAsync(new ExtractionPageEmptyEvent(page.Title), ct);
                }
                else
                {
                    foreach (var evt in events)
                    {
                        await context.AddEventAsync(
                            new EventExtractedEvent(
                                new EventExtractedData(
                                    evt.EventType,
                                    evt.Description,
                                    evt.Year,
                                    evt.Demarcation,
                                    page.Title
                                )
                            ),
                            ct
                        );
                    }
                }

                _logger.LogInformation(
                    "Extracted {EventCount} events from {PageTitle} ({Processed}/{Total})",
                    events.Count,
                    page.Title,
                    processed,
                    pages.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to extract events from {PageTitle}, skipping",
                    page.Title
                );
                _processedPageIds.Add(page.PageId);
                await SaveMongoProgressAsync(ct);

                await context.AddEventAsync(
                    new ExtractionPageFailedEvent(
                        new ExtractionPageFailedData(page.Title, ex.Message)
                    ),
                    ct
                );
            }
        }

        // Store all extracted events in shared state for the review executor
        await context.QueueStateUpdateAsync("events", _accumulatedEvents, "Extraction", ct);

        // Clean up per-page progress — extraction is complete
        await ClearMongoProgressAsync(ct);

        _logger.LogInformation(
            "Extraction complete for {Title}: {EventCount} events from {PageCount} pages",
            characterTitle,
            _accumulatedEvents.Count,
            processed
        );

        return $"Extracted {_accumulatedEvents.Count} events from {processed} pages for {characterTitle}";
    }

    // ── MongoDB per-page progress persistence ───────────────────────────────

    private async Task RestoreFromMongoProgressAsync(CancellationToken ct)
    {
        var doc = await _progressCollection
            .Find(Builders<ExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId))
            .FirstOrDefaultAsync(ct);

        if (doc is not null && doc.ProcessedPageIds.Count > 0)
        {
            // Use MongoDB progress if it has more data than the framework checkpoint
            if (doc.ProcessedPageIds.Count > _processedPageIds.Count)
            {
                _processedPageIds = [.. doc.ProcessedPageIds];
                _accumulatedEvents = [.. doc.Events];
                _logger.LogInformation(
                    "Restored extraction progress from MongoDB: {ProcessedCount} pages, {EventCount} events",
                    _processedPageIds.Count,
                    _accumulatedEvents.Count
                );
            }
        }
    }

    private async Task SaveMongoProgressAsync(CancellationToken ct)
    {
        var doc = new ExtractionProgressDoc
        {
            Id = _characterPageId,
            ProcessedPageIds = _processedPageIds.ToList(),
            Events = _accumulatedEvents,
            UpdatedAt = DateTime.UtcNow,
        };

        await _progressCollection.ReplaceOneAsync(
            Builders<ExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId),
            doc,
            new ReplaceOptions { IsUpsert = true },
            ct
        );
    }

    private async Task ClearMongoProgressAsync(CancellationToken ct)
    {
        await _progressCollection.DeleteOneAsync(
            Builders<ExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId),
            ct
        );
    }

    // ── LLM extraction ──────────────────────────────────────────────────────

    private async Task<List<ExtractedEvent>> ExtractEventsFromPage(
        PageContent page,
        string characterTitle,
        string continuity,
        CancellationToken ct
    )
    {
        var prompt = $"""
            Extract timeline events for the character "{characterTitle}" from this wiki page.
            Only extract events that directly involve or significantly affect {characterTitle}.
            If this page contains no relevant events for {characterTitle}, return an empty events array.

            IMPORTANT: This character belongs to the {continuity} continuity of Star Wars.
            Only extract events consistent with {continuity} continuity. Do not include events,
            characters, or references that belong exclusively to a different continuity.

            Source Page: {page.Title}
            Template: {page.Template ?? "unknown"}

            Infobox Data:
            {page.InfoboxText}

            Article Content:
            {page.ContentSnippet}

            Return structured events with: eventType, description, year (float), demarcation (BBY/ABY),
            dateDescription, location, relatedCharacters (excluding {characterTitle}).

            Event types: Birth, Death, Battle, Duel, War, Marriage, Apprenticeship, Promotion,
            Exile, Capture, Rescue, Discovery, Betrayal, Alliance, Training, Mission,
            Transformation, Founding, Destruction, Other.
            """;

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<PageExtractionSchema>(
                schemaName: "page_events",
                schemaDescription: "Events extracted from a single wiki page"
            ),
        };

        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            chatOptions,
            ct
        );

        var text = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Strip markdown fences if present
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }

        var schema = JsonSerializer.Deserialize<PageExtractionSchema>(text, JsonOptions);
        if (schema?.Events is null || schema.Events.Count == 0)
            return [];

        return schema
            .Events.Select(e => new ExtractedEvent(
                e.EventType ?? "Other",
                e.Description ?? "",
                e.Year,
                e.Demarcation,
                e.DateDescription,
                e.Location,
                e.RelatedCharacters ?? [],
                page.Title,
                page.WikiUrl
            ))
            .ToList();
    }
}

/// <summary>
/// MongoDB document for per-page extraction progress.
/// Saved after each page so extraction survives mid-loop crashes.
/// </summary>
internal sealed class ExtractionProgressDoc
{
    [BsonId]
    public int Id { get; set; } // characterPageId

    public List<int> ProcessedPageIds { get; set; } = [];
    public List<ExtractedEvent> Events { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An event extracted from a single page, with source attribution.
/// Stored in workflow state for the review executor.
/// </summary>
internal sealed record ExtractedEvent(
    string EventType,
    string Description,
    float? Year,
    string? Demarcation,
    string? DateDescription,
    string? Location,
    List<string> RelatedCharacters,
    string SourcePageTitle,
    string SourceWikiUrl
);

/// <summary>
/// Checkpoint state for framework superstep boundaries.
/// </summary>
internal sealed record ExtractionCheckpoint(
    List<int> ProcessedPageIds,
    List<ExtractedEvent> Events
);
