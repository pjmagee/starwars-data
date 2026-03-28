using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Executors;

namespace StarWarsData.Services;

/// <summary>
/// Wrapper to distinguish the character timeline chat client from the default one in DI.
/// </summary>
public sealed class CharacterTimelineChatClient(IChatClient inner) : DelegatingChatClient(inner);

/// <summary>
/// Singleton tracker for character timeline generation progress.
/// Allows the frontend to poll for stage updates during background generation.
/// </summary>
public class CharacterTimelineTracker
{
    private readonly ConcurrentDictionary<int, GenerationStatus> _statuses = new();

    public GenerationStatus? GetStatus(int pageId) => _statuses.GetValueOrDefault(pageId);

    public bool TryStart(int pageId)
    {
        var status = new GenerationStatus
        {
            Stage = GenerationStage.Queued,
            Message = "Queued for generation...",
            StartedAt = DateTime.UtcNow,
        };
        return _statuses.TryAdd(pageId, status);
    }

    public void Update(int pageId, GenerationStage stage, string message)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
            {
                Stage = stage,
                Message = message,
                StartedAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Stage = stage;
                existing.Message = message;
                return existing;
            }
        );
    }

    public void UpdateProgress(
        int pageId,
        GenerationStage stage,
        string message,
        int currentStep,
        int totalSteps,
        string? currentItem = null,
        int eventsExtracted = 0
    )
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
            {
                Stage = stage,
                Message = message,
                StartedAt = DateTime.UtcNow,
                CurrentStep = currentStep,
                TotalSteps = totalSteps,
                CurrentItem = currentItem,
                EventsExtracted = eventsExtracted,
            },
            (_, existing) =>
            {
                existing.Stage = stage;
                existing.Message = message;
                existing.CurrentStep = currentStep;
                existing.TotalSteps = totalSteps;
                existing.CurrentItem = currentItem;
                existing.EventsExtracted = eventsExtracted;
                return existing;
            }
        );
    }

    public void Complete(int pageId, string message) =>
        Update(pageId, GenerationStage.Complete, message);

    public void Fail(int pageId, string error)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
            {
                Stage = GenerationStage.Failed,
                Message = "Generation failed",
                Error = error,
                StartedAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Stage = GenerationStage.Failed;
                existing.Message = "Generation failed";
                existing.Error = error;
                return existing;
            }
        );
    }

    public void Clear(int pageId) => _statuses.TryRemove(pageId, out _);

    public void AddActivityLog(int pageId, ActivityLogEntry entry)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus { ActivityLog = [entry] },
            (_, existing) =>
            {
                existing.ActivityLog.Add(entry);
                return existing;
            }
        );
    }

    public bool IsRunning(int pageId) =>
        _statuses.TryGetValue(pageId, out var s)
        && s.Stage is not (GenerationStage.Complete or GenerationStage.Failed);
}

/// <summary>
/// ETL service that uses a Microsoft Agent Framework sequential workflow to build
/// rich character timelines. Five executors work in sequence:
///   1. PageDiscoveryExecutor — pure C#, queries MongoDB for character + linked pages
///   2. PageBundlerExecutor — pure C#, groups pages into token-budget batches
///   3. BatchExtractionExecutor — one LLM call per batch (~4 calls instead of ~38)
///   4. EventConsolidatorExecutor — pure C#, lightweight deduplication
///   5. EventReviewExecutor — single LLM call to validate and finalize events
/// </summary>
public class CharacterTimelineService
{
    private readonly IMongoClient _mongoClient;
    private readonly SettingsOptions _settings;
    private readonly ILogger<CharacterTimelineService> _logger;
    private readonly IChatClient _chatClient;

    private const string PagesCollection = "Pages";
    private const string TimelinesCollection = "Timelines";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public CharacterTimelineService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<CharacterTimelineService> logger,
        CharacterTimelineChatClient chatClient
    )
    {
        _mongoClient = mongoClient;
        _settings = settings.Value;
        _logger = logger;
        _chatClient = chatClient;
    }

    private IMongoCollection<Page> Pages =>
        _mongoClient.GetDatabase(_settings.PagesDb).GetCollection<Page>(PagesCollection);

    private IMongoCollection<CharacterTimeline> Timelines =>
        _mongoClient
            .GetDatabase(_settings.CharacterTimelinesDb)
            .GetCollection<CharacterTimeline>(TimelinesCollection);

    /// <summary>
    /// Get basic character info from Pages by pageId, annotated with timeline/generation status.
    /// </summary>
    public async Task<CharacterSearchResult?> GetCharacterInfoAsync(
        int pageId,
        CharacterTimelineTracker tracker,
        CancellationToken ct
    )
    {
        var page = await Pages
            .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId))
            .FirstOrDefaultAsync(ct);

        if (page is null)
            return null;

        var hasTimeline = await Timelines
            .Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, pageId))
            .AnyAsync(ct);

        return new CharacterSearchResult
        {
            PageId = page.PageId,
            Title = page.Title,
            ImageUrl = page.Infobox?.ImageUrl,
            WikiUrl = page.WikiUrl,
            Continuity = page.Continuity,
            HasTimeline = hasTimeline,
            GenerationStatus = tracker.GetStatus(pageId),
        };
    }

    /// <summary>
    /// Search character pages and annotate with timeline availability and generation status.
    /// </summary>
    public async Task<List<CharacterSearchResult>> SearchCharactersAsync(
        string query,
        Continuity? continuity,
        CharacterTimelineTracker tracker,
        CancellationToken ct
    )
    {
        var filters = new List<FilterDefinition<Page>>
        {
            Builders<Page>.Filter.Regex(
                "infobox.Template",
                new BsonRegularExpression(":Character$", "i")
            ),
            Builders<Page>.Filter.Regex(
                p => p.Title,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query), "i")
            ),
        };

        if (continuity.HasValue)
            filters.Add(Builders<Page>.Filter.Eq(p => p.Continuity, continuity.Value));

        var characters = await Pages
            .Find(Builders<Page>.Filter.And(filters))
            .SortBy(p => p.Title)
            .Limit(20)
            .ToListAsync(ct);

        if (characters.Count == 0)
            return [];

        var pageIds = characters.Select(c => c.PageId).ToList();
        var existingTimelines = await Timelines
            .Find(Builders<CharacterTimeline>.Filter.In(t => t.CharacterPageId, pageIds))
            .Project(Builders<CharacterTimeline>.Projection.Include(t => t.CharacterPageId))
            .ToListAsync(ct);

        var hasTimeline = new HashSet<int>(
            existingTimelines.Select(t => t["characterPageId"].AsInt32)
        );

        return characters
            .Select(c => new CharacterSearchResult
            {
                PageId = c.PageId,
                Title = c.Title,
                ImageUrl = c.Infobox?.ImageUrl,
                WikiUrl = c.WikiUrl,
                Continuity = c.Continuity,
                HasTimeline = hasTimeline.Contains(c.PageId),
                GenerationStatus = tracker.GetStatus(c.PageId),
            })
            .ToList();
    }

    /// <summary>
    /// Generate timelines for all characters that don't already have one cached.
    /// </summary>
    public async Task GenerateAllTimelinesAsync(CancellationToken ct)
    {
        var characterFilter = Builders<Page>.Filter.Regex(
            "infobox.Template",
            new BsonRegularExpression(":Character$", "i")
        );

        var characters = await Pages
            .Find(characterFilter)
            .Project(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title))
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} character pages to process", characters.Count);

        var processed = 0;
        var skipped = 0;

        foreach (var charDoc in characters)
        {
            ct.ThrowIfCancellationRequested();

            var pageId = charDoc["_id"].AsInt32;
            var title = charDoc["title"].AsString;

            var existing = await Timelines
                .Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, pageId))
                .AnyAsync(ct);

            if (existing)
            {
                skipped++;
                continue;
            }

            try
            {
                await GenerateTimelineAsync(pageId, ct);
                processed++;
                _logger.LogInformation(
                    "Generated timeline for {Title} ({Processed}/{Total}, {Skipped} skipped)",
                    title,
                    processed,
                    characters.Count,
                    skipped
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to generate timeline for {Title} (PageId={PageId})",
                    title,
                    pageId
                );
            }
        }

        _logger.LogInformation(
            "Character timeline generation complete. Processed: {Processed}, Skipped: {Skipped}, Total: {Total}",
            processed,
            skipped,
            characters.Count
        );
    }

    /// <summary>
    /// Generate a timeline for a single character using a 5-executor workflow:
    /// Discovery → Bundler → BatchExtraction → Consolidation → Review.
    /// Each executor uses shared workflow state — no context window overflow.
    /// </summary>
    public async Task GenerateTimelineAsync(
        int characterPageId,
        CancellationToken ct,
        CharacterTimelineTracker? tracker = null
    )
    {
        _logger.LogInformation("Building timeline for PageId={PageId}", characterPageId);

        // ── Create fresh executor instances (state isolation per run) ────────
        var discoveryExecutor = new PageDiscoveryExecutor(
            _mongoClient,
            _settings,
            _logger,
            tracker
        );
        var bundlerExecutor = new PageBundlerExecutor(_logger, tracker, characterPageId);
        var extractionExecutor = new BatchExtractionExecutor(
            _chatClient,
            _logger,
            tracker,
            characterPageId,
            _mongoClient,
            _settings.CharacterTimelinesDb
        );
        var consolidatorExecutor = new EventConsolidatorExecutor(_logger, tracker, characterPageId);
        var reviewExecutor = new EventReviewExecutor(
            _chatClient,
            _logger,
            tracker,
            characterPageId
        );

        // ── Build 5-step sequential workflow ────────────────────────────────
        var workflow = new WorkflowBuilder(discoveryExecutor)
            .AddEdge(discoveryExecutor, bundlerExecutor)
            .AddEdge(bundlerExecutor, extractionExecutor)
            .AddEdge(extractionExecutor, consolidatorExecutor)
            .AddEdge(consolidatorExecutor, reviewExecutor)
            .WithOutputFrom(reviewExecutor)
            .WithName($"CharacterTimeline-{characterPageId}")
            .Build(validateOrphans: true);

        // ── Execute with persistent MongoDB checkpoints ────────────────────
        var checkpointStore = new MongoCheckpointStore(
            _mongoClient,
            _settings.CharacterTimelinesDb
        );
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);
        // Version the session ID so checkpoints from the old 3-executor workflow
        // are automatically ignored (incompatible superstep layout).
        var sessionId = $"character-timeline-v2-{characterPageId}";

        // Clear any stale v1 checkpoints from the old workflow shape
        await checkpointStore.ClearSessionAsync($"character-timeline-{characterPageId}");

        // Check for existing checkpoints to resume from (survives app restarts)
        var existingCheckpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();

        StreamingRun streamingRun;
        if (existingCheckpoints.Count > 0)
        {
            var lastCheckpoint = existingCheckpoints[^1];
            _logger.LogInformation(
                "Resuming workflow from checkpoint {CheckpointId} for PageId={PageId}",
                lastCheckpoint.CheckpointId,
                characterPageId
            );

            tracker?.Update(
                characterPageId,
                GenerationStage.Extracting,
                "Resuming from last checkpoint..."
            );

            streamingRun = await InProcessExecution.ResumeStreamingAsync(
                workflow,
                lastCheckpoint,
                checkpointManager,
                ct
            );
        }
        else
        {
            streamingRun = await InProcessExecution.RunStreamingAsync(
                workflow,
                characterPageId.ToString(),
                checkpointManager,
                sessionId,
                ct
            );
        }

        // ── Consume streaming events and bridge to tracker ──────────────────
        string? responseText = null;
        await foreach (var evt in streamingRun.WatchStreamAsync(ct))
        {
            if (evt is WorkflowOutputEvent outputEvent)
            {
                responseText = outputEvent.As<string>();
            }

            // Bridge custom workflow events to the tracker's activity log
            if (tracker is not null)
                BridgeEventToTracker(tracker, characterPageId, evt);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning(
                "Workflow returned empty response for PageId={PageId}",
                characterPageId
            );
            tracker?.Fail(characterPageId, "Workflow produced no response");
            return;
        }

        // ── Parse and save ──────────────────────────────────────────────────
        // On resume, the discovery executor's HandleAsync was skipped (superstep already completed),
        // so its public properties (Character, DiscoveredSources) are empty.
        // Fall back to loading from MongoDB directly.
        var character =
            discoveryExecutor.Character
            ?? await Pages
                .Find(Builders<Page>.Filter.Eq(p => p.PageId, characterPageId))
                .FirstOrDefaultAsync(ct);

        if (character is null)
        {
            tracker?.Fail(characterPageId, "Character page not found");
            return;
        }

        tracker?.Update(
            characterPageId,
            GenerationStage.Saving,
            $"Saving timeline for {character.Title}..."
        );

        var events = ParseTimelineResponse(responseText, character.Title);
        if (events.Count == 0)
        {
            _logger.LogWarning("No events extracted for {Title}", character.Title);
            tracker?.Fail(characterPageId, "No events could be extracted");
            return;
        }

        events.Sort();

        // On resume, DiscoveredSources is empty — rebuild from the events' source attributions
        var sources =
            discoveryExecutor.DiscoveredSources.Count > 0
                ? discoveryExecutor.DiscoveredSources
                : events
                    .Where(e => e.SourcePageTitle is not null)
                    .Select(e => new SourcePage
                    {
                        Title = e.SourcePageTitle!,
                        WikiUrl = e.SourceWikiUrl ?? "",
                    })
                    .DistinctBy(s => s.Title)
                    .ToList();

        var timeline = new CharacterTimeline
        {
            CharacterPageId = character.PageId,
            CharacterTitle = character.Title,
            CharacterWikiUrl = character.WikiUrl,
            ImageUrl = character.Infobox?.ImageUrl,
            Continuity = character.Continuity,
            Events = events,
            Sources = sources,
            GeneratedAt = DateTime.UtcNow,
            ModelUsed = _settings.CharacterTimelineModel,
        };

        await Timelines.ReplaceOneAsync(
            Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, character.PageId),
            timeline,
            new ReplaceOptions { IsUpsert = true },
            ct
        );

        // Clean up checkpoints — workflow completed successfully
        await checkpointStore.ClearSessionAsync(sessionId);

        tracker?.Complete(
            characterPageId,
            $"Done! {events.Count} events from {sources.Count} sources"
        );

        _logger.LogInformation(
            "Stored timeline for {Title}: {EventCount} events from {SourceCount} source pages",
            character.Title,
            events.Count,
            sources.Count
        );
    }

    /// <summary>
    /// Delete and regenerate a character's timeline.
    /// </summary>
    public async Task RefreshTimelineAsync(int characterPageId, CancellationToken ct)
    {
        // Clear old timeline, stale checkpoints, and extraction progress so we start fresh
        await Timelines.DeleteManyAsync(
            Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId),
            ct
        );

        var checkpointStore = new MongoCheckpointStore(
            _mongoClient,
            _settings.CharacterTimelinesDb
        );
        await checkpointStore.ClearSessionAsync($"character-timeline-v2-{characterPageId}");
        await checkpointStore.ClearSessionAsync($"character-timeline-{characterPageId}"); // clear legacy v1

        var progressCollection = _mongoClient
            .GetDatabase(_settings.CharacterTimelinesDb)
            .GetCollection<BsonDocument>("ExtractionProgress");
        await progressCollection.DeleteOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", characterPageId),
            ct
        );

        await GenerateTimelineAsync(characterPageId, ct);
    }

    /// <summary>
    /// Retrieve a cached timeline for a character.
    /// </summary>
    public async Task<CharacterTimeline?> GetTimelineAsync(
        int characterPageId,
        CancellationToken ct
    )
    {
        return await Timelines
            .Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// List cached character timelines with search, continuity filtering, and pagination.
    /// </summary>
    public async Task<CharacterTimelineListResult> ListTimelinesAsync(
        int page,
        int pageSize,
        string? search,
        Continuity? continuity,
        CancellationToken ct
    )
    {
        var filters = new List<FilterDefinition<CharacterTimeline>>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(
                Builders<CharacterTimeline>.Filter.Regex(
                    t => t.CharacterTitle,
                    new BsonRegularExpression(
                        System.Text.RegularExpressions.Regex.Escape(search),
                        "i"
                    )
                )
            );
        }

        if (continuity.HasValue)
        {
            filters.Add(Builders<CharacterTimeline>.Filter.Eq(t => t.Continuity, continuity.Value));
        }

        var filter =
            filters.Count > 0
                ? Builders<CharacterTimeline>.Filter.And(filters)
                : FilterDefinition<CharacterTimeline>.Empty;

        var total = await Timelines.CountDocumentsAsync(filter, cancellationToken: ct);
        var skip = (page - 1) * pageSize;

        var timelines = await Timelines
            .Find(filter)
            .SortBy(t => t.CharacterTitle)
            .Skip(skip)
            .Limit(pageSize)
            .Project(t => new CharacterTimelineSummary
            {
                CharacterPageId = t.CharacterPageId,
                CharacterTitle = t.CharacterTitle,
                ImageUrl = t.ImageUrl,
                Continuity = t.Continuity,
                EventCount = t.Events.Count,
                GeneratedAt = t.GeneratedAt,
            })
            .ToListAsync(ct);

        return new CharacterTimelineListResult
        {
            Items = timelines,
            Total = (int)total,
            Page = page,
            PageSize = pageSize,
        };
    }

    // ── Event bridging ────────────────────────────────────────────────────

    private static void BridgeEventToTracker(
        CharacterTimelineTracker tracker,
        int pageId,
        WorkflowEvent evt
    )
    {
        var entry = evt switch
        {
            PageDiscoveredEvent e when e.Data is PageDiscoveredData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Discovery",
                EntryType = "page_discovered",
                Summary =
                    d.Relationship == "self"
                        ? $"Character page: {d.Title}"
                        : $"Found {d.Relationship} link: {d.Title}",
                Detail = new
                {
                    d.PageId,
                    d.Title,
                    d.Template,
                    d.Continuity,
                    d.Relationship,
                },
            },
            DiscoveryCompleteEvent e when e.Data is DiscoveryCompleteData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Discovery",
                EntryType = "discovery_complete",
                Summary =
                    $"Discovery complete: {d.TotalPages} pages ({d.IncomingLinks} incoming, {d.OutgoingLinks} outgoing)",
                Detail = d,
            },
            ExtractionPageStartedEvent e when e.Data is ExtractionPageStartedData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Extraction",
                    EntryType = "extraction_started",
                    Summary = $"Extracting from \"{d.PageTitle}\" ({d.PageIndex}/{d.TotalPages})",
                },
            EventExtractedEvent e when e.Data is EventExtractedData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "event_extracted",
                Summary = d.Year.HasValue
                    ? $"[{d.EventType}] {TruncateDescription(d.Description, 80)} ({d.Year:0.##} {d.Demarcation})"
                    : $"[{d.EventType}] {TruncateDescription(d.Description, 80)}",
                Detail = new
                {
                    d.EventType,
                    d.Description,
                    d.Year,
                    d.Demarcation,
                    d.SourcePageTitle,
                },
            },
            BundlingCompleteEvent e when e.Data is BundlingCompleteData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Bundling",
                EntryType = "bundling_complete",
                Summary =
                    $"Bundled {d.TotalPages} pages into {d.BatchCount} batches [{string.Join(", ", d.BatchSizes)}]",
                Detail = d,
            },
            BatchExtractionStartedEvent e when e.Data is BatchExtractionStartedData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Extraction",
                    EntryType = "batch_started",
                    Summary =
                        $"Extracting batch {d.BatchIndex}/{d.TotalBatches} ({d.PageCount} pages: {string.Join(", ", d.PageTitles.Take(3))}{(d.PageTitles.Count > 3 ? "..." : "")})",
                },
            BatchExtractionEmptyEvent e when e.Data is BatchExtractionEmptyData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Extraction",
                    EntryType = "batch_empty",
                    Summary = $"No events found in batch {d.BatchIndex} ({d.PageCount} pages)",
                },
            BatchExtractionFailedEvent e when e.Data is BatchExtractionFailedData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Extraction",
                    EntryType = "batch_failed",
                    Summary = $"Batch {d.BatchIndex} failed ({d.PageCount} pages): {d.Error}",
                },
            ExtractionPageEmptyEvent e => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "extraction_empty",
                Summary = $"No events found in \"{e.Data}\"",
            },
            ExtractionPageFailedEvent e when e.Data is ExtractionPageFailedData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Extraction",
                    EntryType = "extraction_failed",
                    Summary = $"Failed to extract from \"{d.PageTitle}\": {d.Error}",
                },
            ConsolidationCompleteEvent e when e.Data is ConsolidationCompleteData d =>
                new ActivityLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Category = "Consolidation",
                    EntryType = "consolidation_complete",
                    Summary =
                        d.DuplicatesRemoved > 0
                            ? $"Consolidated {d.InputEventCount} → {d.OutputEventCount} events ({d.DuplicatesRemoved} duplicates removed)"
                            : $"Consolidation complete: all {d.OutputEventCount} events unique",
                    Detail = d,
                },
            ReviewCompleteEvent e when e.Data is ReviewCompleteData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Review",
                EntryType = "review_complete",
                Summary =
                    d.EventsRemoved > 0
                        ? $"Review complete: {d.OutputEventCount} events retained, {d.EventsRemoved} removed (duplicates/cross-continuity)"
                        : $"Review complete: all {d.OutputEventCount} events retained",
                Detail = d,
            },
            _ => null,
        };

        if (entry is not null)
            tracker.AddActivityLog(pageId, entry);
    }

    private static string TruncateDescription(string text, int maxLen) =>
        text.Length > maxLen ? text[..maxLen] + "…" : text;

    /// <summary>
    /// Check if a character has pending workflow checkpoints (interrupted generation).
    /// </summary>
    public async Task<bool> HasPendingCheckpointsAsync(
        int characterPageId,
        CancellationToken ct = default
    )
    {
        var checkpointStore = new MongoCheckpointStore(
            _mongoClient,
            _settings.CharacterTimelinesDb
        );
        var sessionId = $"character-timeline-v2-{characterPageId}";
        var checkpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();
        return checkpoints.Count > 0;
    }

    // ── Response parsing ────────────────────────────────────────────────────

    private List<CharacterEvent> ParseTimelineResponse(string responseText, string characterTitle)
    {
        var json = responseText.Trim();

        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            var response = JsonSerializer.Deserialize<TimelineResponseSchema>(json, JsonOptions);
            return response?.Events?.Select(MapToCharacterEvent).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Failed to parse response for {Character}. Response: {Response}",
                characterTitle,
                json[..Math.Min(json.Length, 500)]
            );
            return [];
        }
    }

    private static CharacterEvent MapToCharacterEvent(TimelineEventSchema e) =>
        new()
        {
            EventType = e.EventType ?? "Other",
            Description = e.Description ?? string.Empty,
            Year = e.Year,
            Demarcation = e.Demarcation?.ToUpperInvariant() switch
            {
                "BBY" => Demarcation.Bby,
                "ABY" => Demarcation.Aby,
                _ => Demarcation.Unset,
            },
            DateDescription = e.DateDescription,
            Location = e.Location,
            RelatedCharacters = e.RelatedCharacters ?? [],
            SourcePageTitle = e.SourcePageTitle,
            SourceWikiUrl = e.SourceWikiUrl,
        };
}
