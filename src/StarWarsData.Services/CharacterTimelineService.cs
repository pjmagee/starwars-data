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

    public GenerationStatus? GetStatus(int pageId) =>
        _statuses.GetValueOrDefault(pageId);

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
        _statuses.AddOrUpdate(pageId,
            _ => new GenerationStatus { Stage = stage, Message = message, StartedAt = DateTime.UtcNow },
            (_, existing) => { existing.Stage = stage; existing.Message = message; return existing; });
    }

    public void UpdateProgress(int pageId, GenerationStage stage, string message,
        int currentStep, int totalSteps, string? currentItem = null, int eventsExtracted = 0)
    {
        _statuses.AddOrUpdate(pageId,
            _ => new GenerationStatus
            {
                Stage = stage, Message = message, StartedAt = DateTime.UtcNow,
                CurrentStep = currentStep, TotalSteps = totalSteps,
                CurrentItem = currentItem, EventsExtracted = eventsExtracted,
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
            });
    }

    public void Complete(int pageId, string message) =>
        Update(pageId, GenerationStage.Complete, message);

    public void Fail(int pageId, string error)
    {
        _statuses.AddOrUpdate(pageId,
            _ => new GenerationStatus { Stage = GenerationStage.Failed, Message = "Generation failed", Error = error, StartedAt = DateTime.UtcNow },
            (_, existing) => { existing.Stage = GenerationStage.Failed; existing.Message = "Generation failed"; existing.Error = error; return existing; });
    }

    public void Clear(int pageId) => _statuses.TryRemove(pageId, out _);

    public bool IsRunning(int pageId) =>
        _statuses.TryGetValue(pageId, out var s)
        && s.Stage is not (GenerationStage.Complete or GenerationStage.Failed);
}

/// <summary>
/// ETL service that uses a Microsoft Agent Framework sequential workflow to build
/// rich character timelines. Three custom executors work in sequence:
///   1. PageDiscoveryExecutor — pure C#, queries MongoDB for character + linked pages
///   2. EventExtractionExecutor — per-page LLM calls to extract events (bounded context)
///   3. EventReviewExecutor — single LLM call to deduplicate and validate all events
/// </summary>
public class CharacterTimelineService
{
    private readonly IMongoClient _mongoClient;
    private readonly SettingsOptions _settings;
    private readonly ILogger<CharacterTimelineService> _logger;
    private readonly IChatClient _chatClient;

    private const string PagesCollection = "Pages";
    private const string TimelinesCollection = "Timelines";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CharacterTimelineService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<CharacterTimelineService> logger,
        CharacterTimelineChatClient chatClient)
    {
        _mongoClient = mongoClient;
        _settings = settings.Value;
        _logger = logger;
        _chatClient = chatClient;
    }

    private IMongoCollection<Page> Pages =>
        _mongoClient.GetDatabase(_settings.PagesDb).GetCollection<Page>(PagesCollection);

    private IMongoCollection<CharacterTimeline> Timelines =>
        _mongoClient.GetDatabase(_settings.CharacterTimelinesDb).GetCollection<CharacterTimeline>(TimelinesCollection);

    /// <summary>
    /// Get basic character info from Pages by pageId, annotated with timeline/generation status.
    /// </summary>
    public async Task<CharacterSearchResult?> GetCharacterInfoAsync(
        int pageId, CharacterTimelineTracker tracker, CancellationToken ct)
    {
        var page = await Pages
            .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId))
            .FirstOrDefaultAsync(ct);

        if (page is null) return null;

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
        string query, Continuity? continuity, CharacterTimelineTracker tracker, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<Page>>
        {
            Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression(":Character$", "i")),
            Builders<Page>.Filter.Regex(p => p.Title,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query), "i")),
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

        var hasTimeline = new HashSet<int>(existingTimelines.Select(t => t["characterPageId"].AsInt32));

        return characters.Select(c => new CharacterSearchResult
        {
            PageId = c.PageId,
            Title = c.Title,
            ImageUrl = c.Infobox?.ImageUrl,
            WikiUrl = c.WikiUrl,
            Continuity = c.Continuity,
            HasTimeline = hasTimeline.Contains(c.PageId),
            GenerationStatus = tracker.GetStatus(c.PageId),
        }).ToList();
    }

    /// <summary>
    /// Generate timelines for all characters that don't already have one cached.
    /// </summary>
    public async Task GenerateAllTimelinesAsync(CancellationToken ct)
    {
        var characterFilter = Builders<Page>.Filter.Regex(
            "infobox.Template",
            new BsonRegularExpression(":Character$", "i"));

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
                    title, processed, characters.Count, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate timeline for {Title} (PageId={PageId})", title, pageId);
            }
        }

        _logger.LogInformation(
            "Character timeline generation complete. Processed: {Processed}, Skipped: {Skipped}, Total: {Total}",
            processed, skipped, characters.Count);
    }

    /// <summary>
    /// Generate a timeline for a single character using a 3-executor workflow:
    /// Discovery (pure C#) → Extraction (per-page LLM) → Review (single LLM).
    /// Each executor uses shared workflow state — no context window overflow.
    /// </summary>
    public async Task GenerateTimelineAsync(
        int characterPageId, CancellationToken ct, CharacterTimelineTracker? tracker = null)
    {
        _logger.LogInformation("Building timeline for PageId={PageId}", characterPageId);

        // ── Create fresh executor instances (state isolation per run) ────────
        var discoveryExecutor = new PageDiscoveryExecutor(
            _mongoClient, _settings, _logger, tracker);
        var extractionExecutor = new EventExtractionExecutor(
            _chatClient, _logger, tracker, characterPageId);
        var reviewExecutor = new EventReviewExecutor(
            _chatClient, _logger, tracker, characterPageId);

        // ── Build sequential workflow: Discovery → Extraction → Review ──────
        var workflow = new WorkflowBuilder(discoveryExecutor)
            .AddEdge(discoveryExecutor, extractionExecutor)
            .AddEdge(extractionExecutor, reviewExecutor)
            .WithOutputFrom(reviewExecutor)
            .WithName($"CharacterTimeline-{characterPageId}")
            .Build(validateOrphans: true);

        // ── Execute with persistent MongoDB checkpoints ────────────────────
        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.CharacterTimelinesDb);
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);
        var sessionId = $"character-timeline-{characterPageId}";

        // Check for existing checkpoints to resume from (survives app restarts)
        var existingCheckpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();

        Run run;
        if (existingCheckpoints.Count > 0)
        {
            var lastCheckpoint = existingCheckpoints[^1];
            _logger.LogInformation(
                "Resuming workflow from checkpoint {CheckpointId} for PageId={PageId}",
                lastCheckpoint.CheckpointId, characterPageId);

            tracker?.Update(characterPageId, GenerationStage.Extracting,
                "Resuming from last checkpoint...");

            run = await InProcessExecution.ResumeAsync(
                workflow,
                lastCheckpoint,
                checkpointManager,
                ct);
        }
        else
        {
            run = await InProcessExecution.RunAsync(
                workflow,
                characterPageId.ToString(),
                checkpointManager,
                sessionId,
                ct);
        }

        // ── Extract final output ────────────────────────────────────────────
        string? responseText = null;
        foreach (var evt in run.OutgoingEvents)
        {
            if (evt is WorkflowOutputEvent outputEvent)
            {
                responseText = outputEvent.As<string>();
            }
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("Workflow returned empty response for PageId={PageId}", characterPageId);
            tracker?.Fail(characterPageId, "Workflow produced no response");
            return;
        }

        // ── Parse and save ──────────────────────────────────────────────────
        var character = discoveryExecutor.Character;
        if (character is null)
        {
            tracker?.Fail(characterPageId, "Character page not found");
            return;
        }

        tracker?.Update(characterPageId, GenerationStage.Saving,
            $"Saving timeline for {character.Title}...");

        var events = ParseTimelineResponse(responseText, character.Title);
        if (events.Count == 0)
        {
            _logger.LogWarning("No events extracted for {Title}", character.Title);
            tracker?.Fail(characterPageId, "No events could be extracted");
            return;
        }

        events.Sort();

        var timeline = new CharacterTimeline
        {
            CharacterPageId = character.PageId,
            CharacterTitle = character.Title,
            CharacterWikiUrl = character.WikiUrl,
            ImageUrl = character.Infobox?.ImageUrl,
            Continuity = character.Continuity,
            Events = events,
            Sources = discoveryExecutor.DiscoveredSources,
            GeneratedAt = DateTime.UtcNow,
            ModelUsed = _settings.CharacterTimelineModel,
        };

        await Timelines.ReplaceOneAsync(
            Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, character.PageId),
            timeline,
            new ReplaceOptions { IsUpsert = true },
            ct);

        // Clean up checkpoints — workflow completed successfully
        await checkpointStore.ClearSessionAsync(sessionId);

        tracker?.Complete(characterPageId,
            $"Done! {events.Count} events from {discoveryExecutor.DiscoveredSources.Count} sources");

        _logger.LogInformation(
            "Stored timeline for {Title}: {EventCount} events from {SourceCount} source pages",
            character.Title, events.Count, discoveryExecutor.DiscoveredSources.Count);
    }

    /// <summary>
    /// Delete and regenerate a character's timeline.
    /// </summary>
    public async Task RefreshTimelineAsync(int characterPageId, CancellationToken ct)
    {
        // Clear old timeline and any stale checkpoints so we start fresh
        await Timelines.DeleteManyAsync(
            Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId), ct);

        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.CharacterTimelinesDb);
        await checkpointStore.ClearSessionAsync($"character-timeline-{characterPageId}");

        await GenerateTimelineAsync(characterPageId, ct);
    }

    /// <summary>
    /// Retrieve a cached timeline for a character.
    /// </summary>
    public async Task<CharacterTimeline?> GetTimelineAsync(int characterPageId, CancellationToken ct)
    {
        return await Timelines
            .Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// List cached character timelines with search, continuity filtering, and pagination.
    /// </summary>
    public async Task<CharacterTimelineListResult> ListTimelinesAsync(
        int page, int pageSize, string? search, Continuity? continuity, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<CharacterTimeline>>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(Builders<CharacterTimeline>.Filter.Regex(
                t => t.CharacterTitle,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(search), "i")));
        }

        if (continuity.HasValue)
        {
            filters.Add(Builders<CharacterTimeline>.Filter.Eq(t => t.Continuity, continuity.Value));
        }

        var filter = filters.Count > 0
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
            _logger.LogError(ex, "Failed to parse response for {Character}. Response: {Response}",
                characterTitle, json[..Math.Min(json.Length, 500)]);
            return [];
        }
    }

    private static CharacterEvent MapToCharacterEvent(TimelineEventSchema e) => new()
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
