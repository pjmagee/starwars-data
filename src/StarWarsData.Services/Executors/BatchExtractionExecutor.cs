using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Processes page batches with one LLM call per batch, extracting timeline events.
/// Much more efficient than per-page calls — typically ~4 calls instead of ~38.
///
/// Progress is saved to MongoDB after each batch, so extraction can resume
/// mid-loop after an app restart.
///
/// Superstep 3: Discovery → Bundler → [Extraction] → Consolidation → Review
/// </summary>
internal sealed class BatchExtractionExecutor : Executor<string, string>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;
    private readonly IMongoCollection<BatchExtractionProgressDoc> _progressCollection;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // In-memory state, seeded from MongoDB on HandleAsync entry
    private HashSet<int> _processedBatchIndices = [];
    private List<ExtractedEvent> _accumulatedEvents = [];

    public BatchExtractionExecutor(
        IChatClient chatClient,
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId,
        IMongoClient mongoClient,
        string databaseName
    )
        : base("BatchExtraction")
    {
        _chatClient = chatClient;
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
        _progressCollection = mongoClient
            .GetDatabase(databaseName)
            .GetCollection<BatchExtractionProgressDoc>("ExtractionProgress");
    }

    protected override ValueTask OnCheckpointingAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        return context.QueueStateUpdateAsync(
            "checkpoint",
            new BatchExtractionCheckpoint(_processedBatchIndices.ToList(), _accumulatedEvents),
            "BatchExtraction",
            cancellationToken
        );
    }

    protected override async ValueTask OnCheckpointRestoredAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var checkpoint = await context.ReadStateAsync<BatchExtractionCheckpoint>(
            "checkpoint",
            "BatchExtraction",
            cancellationToken
        );

        if (checkpoint is not null)
        {
            _processedBatchIndices = [.. checkpoint.ProcessedBatchIndices];
            _accumulatedEvents = [.. checkpoint.Events];
            _logger.LogInformation(
                "Restored batch extraction checkpoint: {ProcessedCount} batches, {EventCount} events",
                _processedBatchIndices.Count,
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
        var batches =
            await context.ReadStateAsync<List<PageBatch>>("batches", "Bundler", ct)
            ?? throw new InvalidOperationException("No batches found in Bundler state");

        var characterTitle =
            await context.ReadStateAsync<string>("characterTitle", "Discovery", ct) ?? "Unknown";

        var characterContinuity =
            await context.ReadStateAsync<string>("characterContinuity", "Discovery", ct)
            ?? "Unknown";

        // Restore from MongoDB per-batch progress (survives mid-extraction crashes)
        await RestoreFromMongoProgressAsync(ct);

        var remaining = batches.Where(b => !_processedBatchIndices.Contains(b.BatchIndex)).ToList();

        if (_processedBatchIndices.Count > 0)
        {
            _logger.LogInformation(
                "Resuming batch extraction for {Title}: {Remaining}/{Total} batches remaining ({EventCount} events accumulated)",
                characterTitle,
                remaining.Count,
                batches.Count,
                _accumulatedEvents.Count
            );
        }

        var totalPages = batches.Sum(b => b.Pages.Count);

        _tracker?.UpdateProgress(
            _characterPageId,
            GenerationStage.Extracting,
            $"Extracting events from {remaining.Count} batches ({_processedBatchIndices.Count} already done)...",
            currentStep: _processedBatchIndices.Count,
            totalSteps: batches.Count,
            eventsExtracted: _accumulatedEvents.Count
        );

        _logger.LogInformation(
            "Starting batch extraction for {Title}: {Count} batches remaining",
            characterTitle,
            remaining.Count
        );

        var processed = _processedBatchIndices.Count;

        foreach (var batch in remaining)
        {
            ct.ThrowIfCancellationRequested();

            var batchPageTitles = string.Join(", ", batch.Pages.Select(p => p.Title));

            _tracker?.UpdateProgress(
                _characterPageId,
                GenerationStage.Extracting,
                $"Extracting batch {processed + 1}/{batches.Count} ({batch.Pages.Count} pages)...",
                currentStep: processed,
                totalSteps: batches.Count,
                currentItem: $"Batch {batch.BatchIndex + 1}: {batch.Pages.Count} pages",
                eventsExtracted: _accumulatedEvents.Count
            );

            await context.AddEventAsync(
                new BatchExtractionStartedEvent(
                    new BatchExtractionStartedData(
                        batch.BatchIndex + 1,
                        batches.Count,
                        batch.Pages.Count,
                        batch.Pages.Select(p => p.Title).ToList()
                    )
                ),
                ct
            );

            try
            {
                var events = await ExtractEventsFromBatch(
                    batch,
                    characterTitle,
                    characterContinuity,
                    ct
                );
                _accumulatedEvents.AddRange(events);
                _processedBatchIndices.Add(batch.BatchIndex);
                processed++;

                // Save progress to MongoDB after each batch
                await SaveMongoProgressAsync(ct);

                if (events.Count == 0)
                {
                    await context.AddEventAsync(
                        new BatchExtractionEmptyEvent(
                            new BatchExtractionEmptyData(batch.BatchIndex + 1, batch.Pages.Count)
                        ),
                        ct
                    );
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
                                    evt.SourcePageTitle
                                )
                            ),
                            ct
                        );
                    }
                }

                _logger.LogInformation(
                    "Extracted {EventCount} events from batch {BatchIndex} ({PageCount} pages, {Processed}/{Total})",
                    events.Count,
                    batch.BatchIndex + 1,
                    batch.Pages.Count,
                    processed,
                    batches.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to extract from batch {BatchIndex}, skipping",
                    batch.BatchIndex + 1
                );
                _processedBatchIndices.Add(batch.BatchIndex);
                await SaveMongoProgressAsync(ct);

                await context.AddEventAsync(
                    new BatchExtractionFailedEvent(
                        new BatchExtractionFailedData(
                            batch.BatchIndex + 1,
                            batch.Pages.Count,
                            ex.Message
                        )
                    ),
                    ct
                );
            }
        }

        // Store all extracted events in shared state for the consolidation executor
        await context.QueueStateUpdateAsync("events", _accumulatedEvents, "BatchExtraction", ct);

        // Clean up per-batch progress — extraction is complete
        await ClearMongoProgressAsync(ct);

        _logger.LogInformation(
            "Batch extraction complete for {Title}: {EventCount} events from {BatchCount} batches ({PageCount} pages)",
            characterTitle,
            _accumulatedEvents.Count,
            processed,
            totalPages
        );

        return $"Extracted {_accumulatedEvents.Count} events from {processed} batches ({totalPages} pages) for {characterTitle}";
    }

    // ── MongoDB per-batch progress persistence ──────────────────────────────

    private async Task RestoreFromMongoProgressAsync(CancellationToken ct)
    {
        var doc = await _progressCollection
            .Find(Builders<BatchExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId))
            .FirstOrDefaultAsync(ct);

        if (doc is not null && doc.ProcessedBatchIndices.Count > 0)
        {
            if (doc.ProcessedBatchIndices.Count > _processedBatchIndices.Count)
            {
                _processedBatchIndices = [.. doc.ProcessedBatchIndices];
                _accumulatedEvents = [.. doc.Events];
                _logger.LogInformation(
                    "Restored batch extraction progress from MongoDB: {ProcessedCount} batches, {EventCount} events",
                    _processedBatchIndices.Count,
                    _accumulatedEvents.Count
                );
            }
        }
    }

    private async Task SaveMongoProgressAsync(CancellationToken ct)
    {
        var doc = new BatchExtractionProgressDoc
        {
            Id = _characterPageId,
            ProcessedBatchIndices = _processedBatchIndices.ToList(),
            Events = _accumulatedEvents,
            UpdatedAt = DateTime.UtcNow,
        };

        await _progressCollection.ReplaceOneAsync(
            Builders<BatchExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId),
            doc,
            new ReplaceOptions { IsUpsert = true },
            ct
        );
    }

    private async Task ClearMongoProgressAsync(CancellationToken ct)
    {
        await _progressCollection.DeleteOneAsync(
            Builders<BatchExtractionProgressDoc>.Filter.Eq(d => d.Id, _characterPageId),
            ct
        );
    }

    // ── LLM batch extraction ────────────────────────────────────────────────

    private async Task<List<ExtractedEvent>> ExtractEventsFromBatch(
        PageBatch batch,
        string characterTitle,
        string continuity,
        CancellationToken ct
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"""
            Extract timeline events for the character "{characterTitle}" from the following wiki pages.
            Only extract events that directly involve or significantly affect {characterTitle}.
            For pages with no relevant events, simply skip them.

            IMPORTANT: This character belongs to the {continuity} continuity of Star Wars.
            Only extract events consistent with {continuity} continuity. Do not include events,
            characters, or references that belong exclusively to a different continuity.

            For each event, include the sourcePageTitle field indicating which page it came from.

            Event types: Birth, Death, Battle, Duel, War, Marriage, Apprenticeship, Promotion,
            Exile, Capture, Rescue, Discovery, Betrayal, Alliance, Training, Mission,
            Transformation, Founding, Destruction, Other.

            """
        );

        foreach (var page in batch.Pages)
        {
            sb.AppendLine($"--- PAGE: {page.Title} ---");
            sb.AppendLine($"Template: {page.Template ?? "unknown"}");
            sb.AppendLine($"URL: {page.WikiUrl}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(page.InfoboxText))
            {
                sb.AppendLine("Infobox Data:");
                sb.AppendLine(page.InfoboxText);
            }
            if (!string.IsNullOrWhiteSpace(page.ContentSnippet))
            {
                sb.AppendLine("Article Content:");
                sb.AppendLine(page.ContentSnippet);
            }
            sb.AppendLine();
        }

        sb.AppendLine(
            """
            Return structured events with: eventType, description, year (float), demarcation (BBY/ABY),
            dateDescription, location, relatedCharacters (excluding the main character),
            sourcePageTitle (the exact page title this event came from), sourceWikiUrl.
            """
        );

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<BatchExtractionSchema>(
                schemaName: "batch_events",
                schemaDescription: "Events extracted from a batch of wiki pages"
            ),
        };

        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, sb.ToString())],
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

        var schema = JsonSerializer.Deserialize<BatchExtractionSchema>(text, JsonOptions);
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
                e.SourcePageTitle ?? "Unknown",
                e.SourceWikiUrl ?? ""
            ))
            .ToList();
    }
}

/// <summary>
/// MongoDB document for per-batch extraction progress.
/// Saved after each batch so extraction survives mid-loop crashes.
/// </summary>
internal sealed class BatchExtractionProgressDoc
{
    [BsonId]
    public int Id { get; set; } // characterPageId

    public List<int> ProcessedBatchIndices { get; set; } = [];
    public List<ExtractedEvent> Events { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Checkpoint state for framework superstep boundaries.
/// </summary>
internal sealed record BatchExtractionCheckpoint(
    List<int> ProcessedBatchIndices,
    List<ExtractedEvent> Events
);
