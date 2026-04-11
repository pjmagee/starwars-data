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

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CharacterTimelineService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<CharacterTimelineService> logger, CharacterTimelineChatClient chatClient)
    {
        _mongoClient = mongoClient;
        _settings = settings.Value;
        _logger = logger;
        _chatClient = chatClient;
    }

    private IMongoCollection<Page> Pages => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<Page>(Collections.Pages);

    private IMongoCollection<CharacterTimeline> Timelines => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<CharacterTimeline>(Collections.GenaiCharacterTimelines);

    private IMongoCollection<RelationshipEdge> KgEdges => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    private IMongoCollection<GraphNode> KgNodes => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    /// <summary>
    /// Get basic character info from Pages by pageId, annotated with timeline/generation status.
    /// </summary>
    public async Task<CharacterSearchResult?> GetCharacterInfoAsync(int pageId, CharacterTimelineTracker tracker, CancellationToken ct)
    {
        var page = await Pages.Find(Builders<Page>.Filter.Eq(p => p.PageId, pageId)).FirstOrDefaultAsync(ct);

        if (page is null)
            return null;

        var hasTimeline = await Timelines.Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, pageId)).AnyAsync(ct);

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
    public async Task<List<CharacterSearchResult>> SearchCharactersAsync(string query, Continuity? continuity, CharacterTimelineTracker tracker, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<Page>>
        {
            Builders<Page>.Filter.Eq("infobox.Template", $"{Collections.TemplateUrlPrefix}Character"),
            Builders<Page>.Filter.Regex(p => p.Title, MongoSafe.Regex(query, escape: true)),
        };

        if (continuity.HasValue)
            filters.Add(Builders<Page>.Filter.Eq(p => p.Continuity, continuity.Value));

        var characters = await Pages.Find(Builders<Page>.Filter.And(filters)).SortBy(p => p.Title).Limit(20).ToListAsync(ct);

        if (characters.Count == 0)
            return [];

        var pageIds = characters.Select(c => c.PageId).ToList();
        var existingTimelines = await Timelines
            .Find(Builders<CharacterTimeline>.Filter.In(t => t.CharacterPageId, pageIds))
            .Project(Builders<CharacterTimeline>.Projection.Include(t => t.CharacterPageId))
            .ToListAsync(ct);

        var hasTimeline = new HashSet<int>(existingTimelines.Select(t => t["characterPageId"].AsInt32));

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
        var characterFilter = Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression(":Character$", "i"));

        var characters = await Pages.Find(characterFilter).Project(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title)).ToListAsync(ct);

        _logger.LogInformation("Found {Count} character pages to process", characters.Count);

        var processed = 0;
        var skipped = 0;

        foreach (var charDoc in characters)
        {
            ct.ThrowIfCancellationRequested();

            var pageId = charDoc[MongoFields.Id].AsInt32;
            var title = charDoc[PageBsonFields.Title].AsString;

            var existing = await Timelines.Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, pageId)).AnyAsync(ct);

            if (existing)
            {
                skipped++;
                continue;
            }

            try
            {
                await GenerateTimelineAsync(pageId, ct);
                processed++;
                _logger.LogInformation("Generated timeline for {Title} ({Processed}/{Total}, {Skipped} skipped)", title, processed, characters.Count, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate timeline for {Title} (PageId={PageId})", title, pageId);
            }
        }

        _logger.LogInformation("Character timeline generation complete. Processed: {Processed}, Skipped: {Skipped}, Total: {Total}", processed, skipped, characters.Count);
    }

    /// <summary>
    /// Generate a timeline for a single character using a 5-executor workflow:
    /// Discovery → Bundler → BatchExtraction → Consolidation → Review.
    /// Each executor uses shared workflow state — no context window overflow.
    /// </summary>
    public async Task GenerateTimelineAsync(int characterPageId, CancellationToken ct, CharacterTimelineTracker? tracker = null)
    {
        _logger.LogInformation("Building timeline for PageId={PageId}", characterPageId);

        // ── Create fresh executor instances (state isolation per run) ────────
        var discoveryExecutor = new PageDiscoveryExecutor(_mongoClient, _settings, _logger, tracker);
        var bundlerExecutor = new PageBundlerExecutor(_logger, tracker, characterPageId);
        var extractionExecutor = new BatchExtractionExecutor(_chatClient, _logger, tracker, characterPageId, _mongoClient, _settings.DatabaseName);
        var consolidatorExecutor = new EventConsolidatorExecutor(_logger, tracker, characterPageId);
        var reviewExecutor = new EventReviewExecutor(_chatClient, _logger, tracker, characterPageId);

        // ── Build 5-step sequential workflow ────────────────────────────────
        var workflow = new WorkflowBuilder(discoveryExecutor)
            .AddEdge(discoveryExecutor, bundlerExecutor)
            .AddEdge(bundlerExecutor, extractionExecutor)
            .AddEdge(extractionExecutor, consolidatorExecutor)
            .AddEdge(consolidatorExecutor, reviewExecutor)
            .WithOutputFrom(reviewExecutor)
            .WithName($"CharacterTimeline-{characterPageId}")
            .Build(validateOrphans: true);

        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.DatabaseName);
        var sessionId = $"character-timeline-v2-{characterPageId}";
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);

        // ── Resume-if-possible: list existing checkpoints for this session and resume
        //    from the latest, otherwise start a fresh run. The MongoCheckpointStore returns
        //    checkpoints sorted by createdAt ascending, so the last entry is the newest.
        //    Framework APIs used:
        //      InProcessExecution.RunStreamingAsync(workflow, input, checkpointManager, sessionId, ct)
        //      InProcessExecution.ResumeStreamingAsync(workflow, checkpointInfo, checkpointManager, ct)
        //    See Microsoft.Agents.AI.Workflows 1.0.0-rc5 checkpointing guide.
        var existingCheckpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();

        StreamingRun streamingRun;
        if (existingCheckpoints.Count > 0)
        {
            var latest = existingCheckpoints[^1];
            _logger.LogInformation(
                "Resuming timeline workflow for PageId={PageId} from checkpoint {CheckpointId} ({CheckpointCount} total)",
                characterPageId,
                latest.CheckpointId,
                existingCheckpoints.Count
            );
            tracker?.Update(characterPageId, GenerationStage.Discovering, $"Resuming from checkpoint ({existingCheckpoints.Count} saved)...");
            streamingRun = await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpointManager, ct);
        }
        else
        {
            _logger.LogInformation("Starting fresh timeline workflow for PageId={PageId}", characterPageId);
            streamingRun = await InProcessExecution.RunStreamingAsync(workflow, characterPageId.ToString(), checkpointManager, sessionId, ct);
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

        // ── Once the workflow stream is consumed, always clear checkpoints. ──
        // If post-workflow processing (parsing, saving) fails, the next attempt must start
        // fresh — resuming from the final checkpoint would replay the same broken LLM output
        // in an infinite loop. Checkpoints are only useful for mid-workflow recovery (e.g.
        // crash during batch extraction), not for retrying post-workflow logic.
        await checkpointStore.ClearSessionAsync(sessionId);

        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("Workflow returned empty response for PageId={PageId}", characterPageId);
            tracker?.Fail(characterPageId, "Workflow produced no response");
            return;
        }

        // ── Parse and save ──────────────────────────────────────────────────
        // Character + DiscoveredSources are rehydrated by PageDiscoveryExecutor.OnCheckpointRestoredAsync
        // when the run was resumed, so these instance fields are populated in both paths.
        var character = discoveryExecutor.Character;

        if (character is null)
        {
            tracker?.Fail(characterPageId, "Character snapshot missing from workflow state");
            return;
        }

        tracker?.Update(characterPageId, GenerationStage.Saving, $"Saving timeline for {character.Title}...");

        var events = ParseTimelineResponse(responseText, character.Title);
        if (events.Count == 0)
        {
            _logger.LogWarning("No events extracted for {Title}", character.Title);
            tracker?.Fail(characterPageId, "No events could be extracted");
            return;
        }

        // ── Sort events using KG-backed fallback for null-year entries ──
        await SortEventsWithKgFallbackAsync(events, characterPageId, ct);

        var sources = discoveryExecutor.DiscoveredSources;

        var timeline = new CharacterTimeline
        {
            CharacterPageId = character.PageId,
            CharacterTitle = character.Title,
            CharacterWikiUrl = character.WikiUrl,
            ImageUrl = character.ImageUrl,
            Continuity = character.Continuity,
            Events = events,
            Sources = sources,
            GeneratedAt = DateTime.UtcNow,
            ModelUsed = _settings.CharacterTimelineModel,
        };

        await Timelines.ReplaceOneAsync(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, character.PageId), timeline, new ReplaceOptions { IsUpsert = true }, ct);

        tracker?.Complete(characterPageId, $"Done! {events.Count} events from {sources.Count} sources");

        _logger.LogInformation("Stored timeline for {Title}: {EventCount} events from {SourceCount} source pages", character.Title, events.Count, sources.Count);
    }

    /// <summary>
    /// Delete and regenerate a character's timeline.
    /// </summary>
    public async Task RefreshTimelineAsync(int characterPageId, CancellationToken ct)
    {
        // Clear old timeline, stale checkpoints, and extraction progress so we start fresh
        await Timelines.DeleteManyAsync(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId), ct);

        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.DatabaseName);
        await checkpointStore.ClearSessionAsync($"character-timeline-v2-{characterPageId}");
        await checkpointStore.ClearSessionAsync($"character-timeline-{characterPageId}"); // clear legacy v1

        var progressCollection = _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<BsonDocument>(Collections.GenaiCharacterProgress);
        await progressCollection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq(MongoFields.Id, characterPageId), ct);

        await GenerateTimelineAsync(characterPageId, ct);
    }

    /// <summary>
    /// Retrieve a cached timeline for a character.
    /// </summary>
    public async Task<CharacterTimeline?> GetTimelineAsync(int characterPageId, CancellationToken ct)
    {
        return await Timelines.Find(Builders<CharacterTimeline>.Filter.Eq(t => t.CharacterPageId, characterPageId)).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// List cached character timelines with search, continuity filtering, and pagination.
    /// </summary>
    public async Task<CharacterTimelineListResult> ListTimelinesAsync(int page, int pageSize, string? search, Continuity? continuity, string? sort, string? sortDirection, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<CharacterTimeline>>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(Builders<CharacterTimeline>.Filter.Regex(t => t.CharacterTitle, MongoSafe.Regex(search, escape: true)));
        }

        if (continuity.HasValue)
        {
            filters.Add(Builders<CharacterTimeline>.Filter.Eq(t => t.Continuity, continuity.Value));
        }

        var filter = filters.Count > 0 ? Builders<CharacterTimeline>.Filter.And(filters) : FilterDefinition<CharacterTimeline>.Empty;

        var total = await Timelines.CountDocumentsAsync(filter, cancellationToken: ct);
        var skip = (page - 1) * pageSize;

        var descending = string.Equals(sortDirection, "Descending", StringComparison.OrdinalIgnoreCase);
        var direction = descending ? -1 : 1;

        var sortDoc = sort switch
        {
            "title" => new BsonDocument("characterTitle", direction),
            "continuity" => new BsonDocument("continuity", direction),
            "events" => new BsonDocument("_eventCount", direction),
            "sources" => new BsonDocument("_sourceCount", direction),
            "generated" => new BsonDocument("generatedAt", direction),
            _ => new BsonDocument("characterTitle", 1),
        };

        var timelines = await Timelines
            .Aggregate()
            .Match(filter)
            .AppendStage<BsonDocument>(
                new BsonDocument(
                    "$addFields",
                    new BsonDocument
                    {
                        ["_eventCount"] = new BsonDocument("$size", new BsonDocument("$ifNull", new BsonArray { "$events", new BsonArray() })),
                        ["_sourceCount"] = new BsonDocument("$size", new BsonDocument("$ifNull", new BsonArray { "$sources", new BsonArray() })),
                    }
                )
            )
            .Sort(sortDoc)
            .Skip(skip)
            .Limit(pageSize)
            .Project(
                new BsonDocument
                {
                    ["_id"] = 0,
                    ["characterPageId"] = 1,
                    ["characterTitle"] = 1,
                    ["imageUrl"] = 1,
                    ["continuity"] = 1,
                    ["eventCount"] = "$_eventCount",
                    ["sourceCount"] = "$_sourceCount",
                    ["generatedAt"] = 1,
                }
            )
            .As<CharacterTimelineSummary>()
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

    private static void BridgeEventToTracker(CharacterTimelineTracker tracker, int pageId, WorkflowEvent evt)
    {
        var entry = evt switch
        {
            PageDiscoveredEvent e when e.Data is PageDiscoveredData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Discovery",
                EntryType = "page_discovered",
                Summary = d.Relationship == "self" ? $"Character page: {d.Title}" : $"Found {d.Relationship} link: {d.Title}",
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
                Summary = $"Discovery complete: {d.TotalPages} pages ({d.IncomingLinks} incoming, {d.OutgoingLinks} outgoing, {d.KgLinks} knowledge graph)",
                Detail = d,
            },
            EventExtractedEvent e when e.Data is EventExtractedData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "event_extracted",
                Summary = d.Year.HasValue ? $"[{d.EventType}] {TruncateDescription(d.Description, 80)} ({d.Year:0.##} {d.Demarcation})" : $"[{d.EventType}] {TruncateDescription(d.Description, 80)}",
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
                Summary = $"Bundled {d.TotalPages} pages into {d.BatchCount} batches [{string.Join(", ", d.BatchSizes)}]",
                Detail = d,
            },
            BatchExtractionStartedEvent e when e.Data is BatchExtractionStartedData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_started",
                Summary = $"Extracting batch {d.BatchIndex}/{d.TotalBatches} ({d.PageCount} pages: {string.Join(", ", d.PageTitles.Take(3))}{(d.PageTitles.Count > 3 ? "..." : "")})",
            },
            BatchExtractionEmptyEvent e when e.Data is BatchExtractionEmptyData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_empty",
                Summary = $"No events found in batch {d.BatchIndex} ({d.PageCount} pages)",
            },
            BatchExtractionFailedEvent e when e.Data is BatchExtractionFailedData d => new ActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_failed",
                Summary = $"Batch {d.BatchIndex} failed ({d.PageCount} pages): {d.Error}",
            },
            ConsolidationCompleteEvent e when e.Data is ConsolidationCompleteData d => new ActivityLogEntry
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

    private static string TruncateDescription(string text, int maxLen) => text.Length > maxLen ? text[..maxLen] + "…" : text;

    /// <summary>
    /// Check if a character has pending workflow checkpoints (interrupted generation).
    /// </summary>
    public async Task<bool> HasPendingCheckpointsAsync(int characterPageId, CancellationToken ct = default)
    {
        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.DatabaseName);
        var sessionId = $"character-timeline-v2-{characterPageId}";
        var checkpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();
        return checkpoints.Count > 0;
    }

    // ── KG-backed event sorting ─────────────────────────────────────────────

    /// <summary>
    /// Sort events chronologically, using the KG as a fallback for events the LLM couldn't date.
    /// For each null-year event we look for a KG edge or related entity's GraphNode whose temporal
    /// bounds overlap the event's related characters or location, and use that as a sort hint only
    /// (we populate <see cref="CharacterEvent.InferredYear"/> rather than overwriting Year).
    /// </summary>
    private async Task SortEventsWithKgFallbackAsync(List<CharacterEvent> events, int characterPageId, CancellationToken ct)
    {
        // Fast path: if every event already has a Year, no KG lookup needed.
        if (events.All(e => e.Year.HasValue))
        {
            events.Sort();
            return;
        }

        // Load the character's edges once (both directions, so we can match on FromName OR ToName).
        var edges = await KgEdges
            .Find(Builders<RelationshipEdge>.Filter.Or(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, characterPageId), Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, characterPageId)))
            .ToListAsync(ct);

        // Index edges by the "other side" name, lowercased, for cheap lookup.
        var edgesByCounterpart = edges
            .Where(e => e.FromYear.HasValue || e.ToYear.HasValue)
            .GroupBy(e => (e.FromId == characterPageId ? e.ToName : e.FromName).Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        // Also grab target-node lifespans for any related characters that edges touched — useful when the
        // edge itself has no years but the target entity (a Battle, Organization) does.
        var targetIds = edges.Select(e => e.FromId == characterPageId ? e.ToId : e.FromId).Distinct().ToList();
        var nodesByLowerName = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        if (targetIds.Count > 0)
        {
            var nodes = await KgNodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, targetIds)).ToListAsync(ct);
            foreach (var node in nodes.Where(n => n.StartYear.HasValue || n.EndYear.HasValue))
            {
                nodesByLowerName[node.Name.Trim()] = node;
            }
        }

        foreach (var evt in events.Where(e => !e.Year.HasValue))
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(evt.Location))
                candidates.Add(evt.Location!);
            candidates.AddRange(evt.RelatedCharacters);

            foreach (var candidate in candidates)
            {
                var key = candidate.Trim().ToLowerInvariant();

                if (edgesByCounterpart.TryGetValue(key, out var edgeList))
                {
                    var edge = edgeList.FirstOrDefault(e => e.FromYear.HasValue) ?? edgeList[0];
                    var sortKey = edge.FromYear ?? edge.ToYear;
                    if (sortKey.HasValue)
                    {
                        var (year, dem) = SplitGalactic(sortKey.Value);
                        evt.InferredYear = year;
                        evt.InferredDemarcation = dem;
                        evt.YearSource = $"kg-edge:{edge.Label}→{(edge.FromId == characterPageId ? edge.ToName : edge.FromName)}";
                        break;
                    }
                }

                if (nodesByLowerName.TryGetValue(candidate.Trim(), out var node))
                {
                    var sortKey = node.StartYear ?? node.EndYear;
                    if (sortKey.HasValue)
                    {
                        var (year, dem) = SplitGalactic(sortKey.Value);
                        evt.InferredYear = year;
                        evt.InferredDemarcation = dem;
                        evt.YearSource = $"kg-node:{node.Name}";
                        break;
                    }
                }
            }
        }

        events.Sort();
    }

    private static (int Year, Demarcation Demarcation) SplitGalactic(int sortKey) => sortKey < 0 ? (-sortKey, Demarcation.Bby) : (sortKey, Demarcation.Aby);

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
            _logger.LogError(ex, "Failed to parse response for {Character}. Response: {Response}", characterTitle, json[..Math.Min(json.Length, 500)]);
            return [];
        }
    }

    private static CharacterEvent MapToCharacterEvent(TimelineEventSchema e) =>
        new()
        {
            EventType = e.EventType ?? "Other",
            Description = e.Description ?? string.Empty,
            Narrative = string.IsNullOrWhiteSpace(e.Narrative) ? null : e.Narrative,
            Significance = string.IsNullOrWhiteSpace(e.Significance) ? null : e.Significance,
            PrecedingContext = string.IsNullOrWhiteSpace(e.PrecedingContext) ? null : e.PrecedingContext,
            Consequences = e.Consequences ?? [],
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
