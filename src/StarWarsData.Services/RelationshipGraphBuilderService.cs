using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Orchestrates the relationship graph building process:
/// - Finds unprocessed pages and feeds them to the LLM analyst agent
/// - Provides dashboard progress stats
/// - Queries the persistent graph via $graphLookup
/// - Ensures indexes on the graph collections
/// </summary>
public class RelationshipGraphBuilderService
{
    readonly ILogger<RelationshipGraphBuilderService> _logger;
    readonly IMongoCollection<BsonDocument> _pages;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<RelationshipCrawlState> _crawlState;
    readonly IMongoCollection<RelationshipLabel> _labels;
    readonly IMongoCollection<GraphBatchJob> _batchJobs;
    readonly IChatClient _chatClient;
    readonly RelationshipAnalystToolkit _toolkit;
    readonly OpenAIClient _openAiClient;
    readonly OpenAiStatusService _aiStatus;
    readonly string _graphDb;
    readonly string _model;

    public RelationshipGraphBuilderService(
        ILogger<RelationshipGraphBuilderService> logger,
        IOptions<SettingsOptions> settings,
        IMongoClient mongoClient,
        RelationshipAnalystToolkit toolkit,
        OpenAIClient openAiClient,
        OpenAiStatusService aiStatus,
        [FromKeyedServices("relationship-analyst")] IChatClient chatClient
    )
    {
        _logger = logger;
        _aiStatus = aiStatus;
        _graphDb = settings.Value.RelationshipGraphDb;
        _model = settings.Value.RelationshipAnalystModel;
        _pages = mongoClient
            .GetDatabase(settings.Value.PagesDb)
            .GetCollection<BsonDocument>("Pages");
        var graphDatabase = mongoClient.GetDatabase(_graphDb);
        _edges = graphDatabase.GetCollection<RelationshipEdge>("edges");
        _crawlState = graphDatabase.GetCollection<RelationshipCrawlState>("crawl_state");
        _labels = graphDatabase.GetCollection<RelationshipLabel>("labels");
        _batchJobs = graphDatabase.GetCollection<GraphBatchJob>("batch_jobs");
        _toolkit = toolkit;
        _openAiClient = openAiClient;
        _chatClient = chatClient;
    }

    const string ExtractionSystemPrompt = """
        You are a relationship extraction engine for a Star Wars knowledge graph.
        You will be given a wiki page's content, its linked entities, and the existing label vocabulary.
        Your job is to extract ALL meaningful relationships and return them as structured JSON.

        RULES:
        - ALWAYS prefer existing labels over inventing new ones
        - Only create a new label when nothing in the existing vocabulary fits
        - Set shouldSkip=true for pages with no meaningful relationships (redirects, stubs, disambiguation, list pages)
        - Weight reflects confidence: explicit textual statements get 0.9+, inferred from context 0.6-0.8,
          inferred from infobox links alone 0.5
        - Evidence must be a brief quote or paraphrase from the article supporting the relationship
        - Extract relationships of ALL kinds: familial, political, military, economic, geographic,
          organizational, adversarial, master/apprentice, species/homeworld, etc.
        - Each entity in a relationship MUST have a valid PageId from the LINKED ENTITIES list.
          Do NOT invent PageIds. If an entity isn't in the linked entities, skip that relationship.
        - Use the entity's infobox type (Character, Organization, CelestialBody, etc.) for fromType/toType
        - Do NOT duplicate edges that already exist (listed under EXISTING EDGES)
        """;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Process a batch of unprocessed pages. Called by the Hangfire recurring job.
    /// </summary>
    /// <summary>
    /// Process all unprocessed pages by running batches until none remain.
    /// Used for initial population and the daily recurring job.
    /// </summary>
    public async Task ProcessAllAsync(int batchSize = 100, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Relationship graph builder: processing all unprocessed pages in batches of {BatchSize}",
            batchSize
        );

        await EnsureIndexesAsync(ct);

        int totalProcessed = 0;
        int totalFailed = 0;
        int batchNumber = 0;

        while (!ct.IsCancellationRequested)
        {
            batchNumber++;
            var (processed, failed, remaining) = await ProcessBatchAsync(batchSize, ct);
            totalProcessed += processed;
            totalFailed += failed;

            _logger.LogInformation(
                "Relationship graph builder: batch {Batch} complete. Batch={Processed}/{Failed}, Total={TotalProcessed}/{TotalFailed}, Remaining≈{Remaining}",
                batchNumber,
                processed,
                failed,
                totalProcessed,
                totalFailed,
                remaining
            );

            if (processed == 0 && failed == 0)
                break; // Nothing left to process
        }

        _logger.LogInformation(
            "Relationship graph builder: all batches complete. TotalProcessed={TotalProcessed}, TotalFailed={TotalFailed}",
            totalProcessed,
            totalFailed
        );
    }

    /// <summary>
    /// Process a single batch of unprocessed pages. Returns (processed, failed, estimatedRemaining).
    /// </summary>
    /// <summary>
    /// Maximum number of pages to process concurrently within a batch.
    /// Each page requires one LLM call — this controls how many run in parallel.
    /// </summary>
    const int MaxConcurrency = 5;

    public async Task<(int processed, int failed, int remaining)> ProcessBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default
    )
    {
        _logger.LogInformation(
            "Relationship graph builder: starting batch of {BatchSize} (concurrency={Concurrency})",
            batchSize,
            MaxConcurrency
        );

        // Reset pages stuck in "Processing" for more than 10 minutes (orphaned from previous runs)
        var staleThreshold = DateTime.UtcNow.AddMinutes(-10);
        var staleFilter = Builders<RelationshipCrawlState>.Filter.And(
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing),
            Builders<RelationshipCrawlState>.Filter.Or(
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.ProcessedAt, null),
                Builders<RelationshipCrawlState>.Filter.Lt(s => s.ProcessedAt, staleThreshold)
            )
        );
        var staleReset = await _crawlState.DeleteManyAsync(staleFilter, ct);
        if (staleReset.DeletedCount > 0)
            _logger.LogWarning(
                "Reset {Count} stale 'Processing' pages from previous run",
                staleReset.DeletedCount
            );

        // Find pages that haven't been processed yet
        var processedIds = await _crawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Empty)
            .Project(s => s.PageId)
            .ToListAsync(ct);

        var processedSet = new HashSet<int>(processedIds);

        // Get pages with infoboxes that haven't been processed, prioritizing high-link pages
        var pipeline = new[]
        {
            new BsonDocument(
                "$match",
                new BsonDocument
                {
                    { "infobox", new BsonDocument("$ne", BsonNull.Value) },
                    { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
                }
            ),
            new BsonDocument(
                "$addFields",
                new BsonDocument(
                    "linkCount",
                    new BsonDocument(
                        "$sum",
                        new BsonDocument(
                            "$map",
                            new BsonDocument
                            {
                                { "input", "$infobox.Data" },
                                { "as", "d" },
                                {
                                    "in",
                                    new BsonDocument(
                                        "$size",
                                        new BsonDocument(
                                            "$ifNull",
                                            new BsonArray { "$$d.Links", new BsonArray() }
                                        )
                                    )
                                },
                            }
                        )
                    )
                )
            ),
            new BsonDocument("$sort", new BsonDocument("linkCount", -1)),
            new BsonDocument("$limit", batchSize * 3), // Fetch extra to account for already-processed
            new BsonDocument(
                "$project",
                new BsonDocument
                {
                    { "_id", 1 },
                    { "title", 1 },
                    { "infobox.Template", 1 },
                    { "linkCount", 1 },
                }
            ),
        };

        var candidates = await _pages
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var batch = candidates
            .Where(d => !processedSet.Contains(d["_id"].AsInt32))
            .Take(batchSize)
            .ToList();

        _logger.LogInformation("Relationship graph builder: {Count} pages to process", batch.Count);

        // Pre-fetch existing labels once for the whole batch
        var existingLabels = await _toolkit.GetExistingLabels();

        int processed = 0;
        int failed = 0;

        // Process pages concurrently using a semaphore to limit parallelism
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = batch.Select(async doc =>
        {
            try
            {
                await semaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return; // Cancellation requested while waiting for a slot
            }

            try
            {
                var success = await ProcessSinglePageAsync(doc, existingLabels, ct);
                if (success)
                    Interlocked.Increment(ref processed);
                else
                    Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Relationship graph builder: batch complete. Processed={Processed}, Failed={Failed}",
            processed,
            failed
        );

        // Estimate remaining unprocessed pages (don't fail on cancellation — these are just stats)
        try
        {
            var totalProcessedIds = await _crawlState.CountDocumentsAsync(
                Builders<RelationshipCrawlState>.Filter.Empty
            );
            var totalEligible = await _pages.CountDocumentsAsync(
                new BsonDocument
                {
                    { "infobox", new BsonDocument("$ne", BsonNull.Value) },
                    { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
                }
            );
            return (processed, failed, (int)(totalEligible - totalProcessedIds));
        }
        catch (OperationCanceledException)
        {
            return (processed, failed, -1);
        }
    }

    /// <summary>
    /// Process a single page: fetch context, call LLM, store edges.
    /// Returns true on success, false on failure.
    /// </summary>
    async Task<bool> ProcessSinglePageAsync(
        BsonDocument doc,
        string existingLabels,
        CancellationToken ct
    )
    {
        var pageId = doc["_id"].AsInt32;
        var title = doc.Contains("title") ? doc["title"].AsString : $"Page {pageId}";
        var template =
            doc.Contains("infobox") && doc["infobox"].AsBsonDocument.Contains("Template")
                ? RecordService.SanitizeTemplateName(doc["infobox"]["Template"].AsString)
                : "Unknown";
        var pageContinuity = doc.Contains("continuity")
            && Enum.TryParse<Continuity>(doc["continuity"].AsString, true, out var pc)
                ? pc
                : Continuity.Unknown;

        try
        {
            _logger.LogInformation("Processing page {PageId}: {Title}", pageId, title);

            // Mark as processing with timestamp for stale detection
            await _crawlState.ReplaceOneAsync(
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                new RelationshipCrawlState
                {
                    PageId = pageId,
                    Name = title,
                    Type = template,
                    Continuity = pageContinuity,
                    Status = CrawlStatus.Processing,
                    ProcessedAt = DateTime.UtcNow,
                },
                new ReplaceOptions { IsUpsert = true },
                ct
            );

            var prompt = await BuildPromptForPageAsync(pageId, existingLabels);

            var chatOptions = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<RelationshipExtractionResponse>(
                    schemaName: "relationship_extraction",
                    schemaDescription: "Extracted relationships from a Star Wars wiki page"
                ),
            };

            // Single LLM call — no tool-calling round-trips
            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, ExtractionSystemPrompt),
                        new ChatMessage(ChatRole.User, prompt),
                    ],
                    chatOptions,
                    ct
                );
                _aiStatus.RecordSuccess("RelationshipGraph");
            }
            catch (Exception ex)
            {
                _aiStatus.RecordError("RelationshipGraph", ex);
                throw;
            }

            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await _toolkit.SkipPage(pageId, "Empty LLM response", title, template);
                return true;
            }

            // Strip markdown fences if present
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                var lastFence = text.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                    text = text[(firstNewline + 1)..lastFence].Trim();
            }

            var result = JsonSerializer.Deserialize<RelationshipExtractionResponse>(text, JsonOpts);

            if (result is null || result.ShouldSkip)
            {
                await _toolkit.SkipPage(
                    pageId,
                    result?.SkipReason ?? "No relationships",
                    title,
                    template
                );
            }
            else if (result.Edges is { Count: > 0 })
            {
                var edgeDtos = result
                    .Edges.Select(e => new EdgeDto
                    {
                        FromId = e.FromId,
                        FromName = e.FromName,
                        FromType = e.FromType,
                        ToId = e.ToId,
                        ToName = e.ToName,
                        ToType = e.ToType,
                        Label = e.Label,
                        ReverseLabel = e.ReverseLabel,
                        Weight = e.Weight,
                        Evidence = e.Evidence,
                        Continuity = e.Continuity,
                    })
                    .ToList();

                var storeResult = await _toolkit.StoreEdges(pageId, edgeDtos);
                _logger.LogInformation(
                    "Page {PageId}: stored edges. Result: {Result}",
                    pageId,
                    storeResult
                );

                await _toolkit.MarkProcessed(pageId, edgeDtos.Count, title, template, pageContinuity.ToString());
            }
            else
            {
                await _toolkit.SkipPage(pageId, "No edges extracted", title, template);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Processing cancelled for page {PageId}: {Title}",
                pageId,
                title
            );
            return true; // Not a failure — just cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process page {PageId}: {Title}", pageId, title);

            try
            {
                await _crawlState.ReplaceOneAsync(
                    Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                    new RelationshipCrawlState
                    {
                        PageId = pageId,
                        Name = title,
                        Type = template,
                        Continuity = pageContinuity,
                        Status = CrawlStatus.Failed,
                        Error = ex.Message[..Math.Min(ex.Message.Length, 500)],
                        ProcessedAt = DateTime.UtcNow,
                    },
                    new ReplaceOptions { IsUpsert = true },
                    ct
                );
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Failed to record error state for page {PageId}", pageId);
            }

            return false;
        }
    }

    // ── Shared prompt building ────────────────────────────────────────────

    /// <summary>
    /// Build the user prompt for a single page (used by both real-time and batch paths).
    /// </summary>
    async Task<string> BuildPromptForPageAsync(int pageId, string existingLabels)
    {
        // Pre-fetch all context in parallel (3 independent MongoDB queries)
        var pageContentTask = _toolkit.GetPageContent(pageId);
        var linkedPagesTask = _toolkit.GetLinkedPages(pageId, limit: 200);
        var existingEdgesTask = _toolkit.GetEntityEdges(pageId, limit: 100);
        await Task.WhenAll(pageContentTask, linkedPagesTask, existingEdgesTask);

        var prompt = new StringBuilder();
        prompt.AppendLine("## EXISTING LABEL VOCABULARY (reuse these)");
        prompt.AppendLine(existingLabels);
        prompt.AppendLine();
        prompt.AppendLine("## PAGE CONTENT");
        prompt.AppendLine(await pageContentTask);
        prompt.AppendLine();
        prompt.AppendLine("## LINKED ENTITIES (only use PageIds from this list)");
        prompt.AppendLine(await linkedPagesTask);
        prompt.AppendLine();
        prompt.AppendLine("## EXISTING EDGES (do not duplicate these)");
        prompt.AppendLine(await existingEdgesTask);
        prompt.AppendLine();
        prompt.AppendLine("Extract ALL meaningful relationships from this page.");
        return prompt.ToString();
    }

    // ── OpenAI Batch API workflow ──────────────────────────────────────────

    /// <summary>
    /// Maximum pages per OpenAI batch (API limit is 50,000 requests).
    /// Kept lower to stay within the 200 MB file size limit per model.
    /// </summary>
    const int MaxPagesPerBatch = 500;

    /// <summary>
    /// Maximum JSONL file size in bytes before we stop adding pages.
    /// OpenAI's limit is 200 MB (209,715,200 bytes) — we target 150 MB for safety.
    /// </summary>
    const long MaxBatchFileSize = 150 * 1024 * 1024;

    /// <summary>
    /// Maximum number of batches allowed in-flight before we stop submitting.
    /// Prevents hammering OpenAI's enqueued token quota.
    /// </summary>
    const int MaxConcurrentBatches = 3;

    /// <summary>
    /// Concurrency for prompt preparation (MongoDB reads only, no LLM calls).
    /// </summary>
    const int PrepConcurrency = 20;

    /// <summary>
    /// Submit unprocessed pages to the OpenAI Batch API.
    /// Submits ONE batch at a time to avoid hitting the enqueued token quota.
    /// Call repeatedly (via Hangfire) to drip-feed batches as quota frees up.
    /// Returns the batch job IDs created (0 or 1).
    /// </summary>
    public async Task<List<string>> SubmitBatchAsync(CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);

        // First, release orphaned pages from old failed/stuck batches
        await CleanupFailedBatchesAsync(ct);

        // Find unprocessed pages
        var processedIds = await _crawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Empty)
            .Project(s => s.PageId)
            .ToListAsync(ct);
        var processedSet = new HashSet<int>(processedIds);

        // Also exclude pages already in a pending/in-progress batch
        var pendingBatches = await _batchJobs
            .Find(
                Builders<GraphBatchJob>.Filter.In(
                    b => b.Status,
                    [
                        GraphBatchStatus.Preparing,
                        GraphBatchStatus.Submitted,
                        GraphBatchStatus.InProgress,
                    ]
                )
            )
            .ToListAsync(ct);
        var batchedPageIds = new HashSet<int>(pendingBatches.SelectMany(b => b.PageIds));

        // If there are already too many in-flight batches, don't submit more
        if (pendingBatches.Count >= MaxConcurrentBatches)
        {
            _logger.LogInformation(
                "Batch API: {Count} batches already in-flight (max {Max}), skipping submission",
                pendingBatches.Count,
                MaxConcurrentBatches
            );
            return [];
        }

        var candidates = await _pages
            .Find(
                new BsonDocument
                {
                    { "infobox", new BsonDocument("$ne", BsonNull.Value) },
                    { "infobox.Template", new BsonDocument("$ne", BsonNull.Value) },
                }
            )
            .Project(
                new BsonDocument
                {
                    { "_id", 1 },
                    { "title", 1 },
                    { "infobox.Template", 1 },
                }
            )
            .ToListAsync(ct);

        var unprocessed = candidates
            .Where(d =>
            {
                var id = d["_id"].AsInt32;
                return !processedSet.Contains(id) && !batchedPageIds.Contains(id);
            })
            .Take(MaxPagesPerBatch) // Only take one batch worth
            .ToList();

        _logger.LogInformation("Batch API: {Count} unprocessed pages found", unprocessed.Count);

        if (unprocessed.Count == 0)
            return [];

        // Submit a single batch — Hangfire will call us again for the next one
        var jobId = await SubmitSingleBatchAsync(unprocessed, ct);
        return jobId != null ? [jobId] : [];
    }

    /// <summary>
    /// Submit a single batch of pages to the OpenAI Batch API.
    /// </summary>
    async Task<string?> SubmitSingleBatchAsync(List<BsonDocument> pages, CancellationToken ct)
    {
        var batchJob = new GraphBatchJob
        {
            Status = GraphBatchStatus.Preparing,
            Model = _model,
            TotalRequests = pages.Count,
            PageIds = pages.Select(d => d["_id"].AsInt32).ToList(),
        };
        await _batchJobs.InsertOneAsync(batchJob, cancellationToken: ct);

        try
        {
            _logger.LogInformation(
                "Batch {BatchId}: preparing {Count} requests",
                batchJob.Id,
                pages.Count
            );

            // Pre-fetch existing labels once for the whole batch
            var existingLabels = await _toolkit.GetExistingLabels();

            // Build JSON schema for structured output
            var jsonSchema = BuildJsonSchema();

            // Stream JSONL to a temp file to avoid OOM on large batches.
            // Prompts are prepared in parallel but written sequentially to disk.
            var tempPath = Path.GetTempFileName();
            try
            {
                var semaphore = new SemaphoreSlim(PrepConcurrency);

                await using (
                    var fileStream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024
                    )
                )
                await using (
                    var writer = new StreamWriter(fileStream, new UTF8Encoding(false))
                    {
                        NewLine = "\n",
                    }
                )
                {
                    // Process pages sequentially for writes, but prep prompts in small parallel batches.
                    // Stop early if the file exceeds the size limit.
                    var writtenCount = 0;
                    var fileSizeLimitHit = false;

                    foreach (var chunk in pages.Chunk(PrepConcurrency))
                    {
                        if (fileSizeLimitHit)
                            break;

                        var tasks = chunk.Select(async doc =>
                        {
                            await semaphore.WaitAsync(ct);
                            try
                            {
                                var pageId = doc["_id"].AsInt32;
                                var prompt = await BuildPromptForPageAsync(pageId, existingLabels);
                                return BuildBatchRequestLine($"page-{pageId}", prompt, jsonSchema);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });

                        var lines = await Task.WhenAll(tasks);
                        foreach (var line in lines)
                        {
                            if (line != null)
                            {
                                await writer.WriteLineAsync(line);
                                writtenCount++;
                            }
                        }

                        // Flush and check file size after each parallel chunk
                        await writer.FlushAsync();
                        if (fileStream.Length >= MaxBatchFileSize)
                        {
                            _logger.LogWarning(
                                "Batch {BatchId}: file size {Size:N0} bytes reached limit after {Count} pages, stopping early",
                                batchJob.Id,
                                fileStream.Length,
                                writtenCount
                            );
                            fileSizeLimitHit = true;
                        }
                    }

                    // Update actual request count and page IDs if we stopped early
                    if (writtenCount < pages.Count)
                    {
                        batchJob.TotalRequests = writtenCount;
                        batchJob.PageIds = batchJob.PageIds.Take(writtenCount).ToList();
                    }
                }

                var fileSize = new FileInfo(tempPath).Length;

                // Upload temp file to OpenAI Files API
                var fileClient = _openAiClient.GetOpenAIFileClient();
                await using var uploadStream = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read
                );
                var uploadResult = await fileClient.UploadFileAsync(
                    uploadStream,
                    $"kg_batch_{batchJob.Id}.jsonl",
                    FileUploadPurpose.Batch,
                    ct
                );

                batchJob.InputFileId = uploadResult.Value.Id;

                _logger.LogInformation(
                    "Batch {BatchId}: uploaded {Size:N0} bytes as file {FileId}",
                    batchJob.Id,
                    fileSize,
                    batchJob.InputFileId
                );
            }
            finally
            {
                File.Delete(tempPath);
            }

            // Create the batch via protocol API
#pragma warning disable OPENAI001 // Experimental API
            var batchClient = _openAiClient.GetBatchClient();
            var createRequest = new CreateBatchRequest
            {
                InputFileId = batchJob.InputFileId,
                Metadata = new Dictionary<string, string> { ["batchJobId"] = batchJob.Id },
            };
            var createBody = BinaryContent.Create(BinaryData.FromObjectAsJson(createRequest));
            var batchOp = await batchClient.CreateBatchAsync(createBody, waitUntilCompleted: false);
#pragma warning restore OPENAI001

            _aiStatus.RecordSuccess("BatchAPI");
            batchJob.OpenAiBatchId = batchOp.BatchId;
            batchJob.Status = GraphBatchStatus.Submitted;
            batchJob.SubmittedAt = DateTime.UtcNow;

            await _batchJobs.ReplaceOneAsync(
                Builders<GraphBatchJob>.Filter.Eq(b => b.Id, batchJob.Id),
                batchJob,
                cancellationToken: ct
            );

            _logger.LogInformation(
                "Batch {BatchId}: submitted as OpenAI batch {OpenAiBatchId} with {Count} requests",
                batchJob.Id,
                batchJob.OpenAiBatchId,
                batchJob.TotalRequests
            );

            // Mark all pages as Processing only AFTER successful submission,
            // so failed submissions don't orphan pages in Processing status.
            var crawlUpdates = batchJob
                .PageIds.Select(pageId => new ReplaceOneModel<RelationshipCrawlState>(
                    Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                    new RelationshipCrawlState
                    {
                        PageId = pageId,
                        Status = CrawlStatus.Processing,
                        ProcessedAt = DateTime.UtcNow,
                    }
                )
                {
                    IsUpsert = true,
                })
                .ToList();

            if (crawlUpdates.Count > 0)
                await _crawlState.BulkWriteAsync(crawlUpdates, cancellationToken: ct);

            return batchJob.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch {BatchId}: failed to submit", batchJob.Id);
            batchJob.Status = GraphBatchStatus.Failed;
            batchJob.Error = ex.Message[..Math.Min(ex.Message.Length, 500)];
            await _batchJobs.ReplaceOneAsync(
                Builders<GraphBatchJob>.Filter.Eq(b => b.Id, batchJob.Id),
                batchJob
            );

            // Clean up any crawl_state entries that were created for this failed batch
            // so those pages become eligible for future batches.
            await _crawlState.DeleteManyAsync(
                Builders<RelationshipCrawlState>.Filter.And(
                    Builders<RelationshipCrawlState>.Filter.In(
                        s => s.PageId,
                        batchJob.PageIds
                    ),
                    Builders<RelationshipCrawlState>.Filter.Eq(
                        s => s.Status,
                        CrawlStatus.Processing
                    )
                ),
                ct
            );

            return null;
        }
    }

    /// <summary>
    /// Check all pending batches and process any that have completed.
    /// Called by Hangfire recurring job.
    /// </summary>
    public async Task CheckBatchesAsync(CancellationToken ct = default)
    {
        var pendingFilter = Builders<GraphBatchJob>.Filter.In(
            b => b.Status,
            [GraphBatchStatus.Submitted, GraphBatchStatus.InProgress]
        );

        var pendingBatches = await _batchJobs.Find(pendingFilter).ToListAsync(ct);

        if (pendingBatches.Count == 0)
        {
            _logger.LogDebug("No pending batches to check");
            return;
        }

#pragma warning disable OPENAI001 // Experimental API
        var batchClient = _openAiClient.GetBatchClient();
#pragma warning restore OPENAI001

        foreach (var job in pendingBatches)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // Use protocol-level API since Status/RequestCounts are internal in the SDK
                var result = await batchClient.GetBatchAsync(
                    job.OpenAiBatchId,
                    (RequestOptions?)null
                );
                var batch = JsonSerializer.Deserialize<OpenAiBatchResponse>(
                    result.GetRawResponse().Content,
                    JsonOpts
                )!;

                var status = batch.Status;
                var completedCount = batch.RequestCounts?.Completed ?? 0;
                var totalCount = batch.RequestCounts?.Total ?? 0;

                _logger.LogInformation(
                    "Batch {BatchId} ({OpenAiId}): status={Status}, completed={Completed}/{Total}",
                    job.Id,
                    job.OpenAiBatchId,
                    status,
                    completedCount,
                    totalCount
                );

                if (status is "completed" or "expired" or "cancelled")
                {
                    job.OutputFileId = batch.OutputFileId;
                    job.ErrorFileId = batch.ErrorFileId;
                    job.CompletedAt = DateTime.UtcNow;

                    if (status == "completed")
                    {
                        job.Status = GraphBatchStatus.Completed;
                        await _batchJobs.ReplaceOneAsync(
                            Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                            job,
                            cancellationToken: ct
                        );

                        // Process results immediately
                        await ProcessBatchResultsAsync(job, ct);
                    }
                    else if (!string.IsNullOrEmpty(batch.OutputFileId))
                    {
                        // Expired/cancelled batches may still have partial results
                        // in the output file — process what completed, then release the rest.
                        _logger.LogWarning(
                            "Batch {BatchId}: ended with status {Status}, processing partial results",
                            job.Id,
                            status
                        );
                        job.Status = GraphBatchStatus.Completed;
                        job.Error = $"Batch ended with status: {status} (partial results recovered)";
                        await _batchJobs.ReplaceOneAsync(
                            Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                            job,
                            cancellationToken: ct
                        );

                        await ProcessBatchResultsAsync(job, ct);

                        // Release any pages still stuck in Processing (the ones that expired/were cancelled)
                        await ReleaseFailedBatchPagesAsync(job, ct);
                    }
                    else
                    {
                        job.Status = GraphBatchStatus.Failed;
                        job.Error = $"Batch ended with status: {status}";
                        await _batchJobs.ReplaceOneAsync(
                            Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                            job,
                            cancellationToken: ct
                        );
                        await ReleaseFailedBatchPagesAsync(job, ct);
                    }
                }
                else if (status == "failed")
                {
                    job.Status = GraphBatchStatus.Failed;
                    job.Error = "OpenAI batch failed";
                    job.CompletedAt = DateTime.UtcNow;
                    await _batchJobs.ReplaceOneAsync(
                        Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                        job,
                        cancellationToken: ct
                    );
                    await ReleaseFailedBatchPagesAsync(job, ct);
                }
                else if (job.Status != GraphBatchStatus.InProgress)
                {
                    // Update to InProgress if it's now running
                    job.Status = GraphBatchStatus.InProgress;
                    await _batchJobs.ReplaceOneAsync(
                        Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                        job,
                        cancellationToken: ct
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to check batch {BatchId} ({OpenAiId})",
                    job.Id,
                    job.OpenAiBatchId
                );
            }
        }
    }

    /// <summary>
    /// Find all Failed/Preparing batches that never completed and release their orphaned pages.
    /// Also cleans up stale "Preparing" batches older than 1 hour (stuck before submission).
    /// Called automatically at the start of SubmitBatchAsync.
    /// </summary>
    public async Task CleanupFailedBatchesAsync(CancellationToken ct = default)
    {
        // Find batches that are Failed or stuck in Preparing for over an hour
        var failedFilter = Builders<GraphBatchJob>.Filter.Eq(
            b => b.Status,
            GraphBatchStatus.Failed
        );
        var stalePreparingFilter = Builders<GraphBatchJob>.Filter.And(
            Builders<GraphBatchJob>.Filter.Eq(b => b.Status, GraphBatchStatus.Preparing),
            Builders<GraphBatchJob>.Filter.Lt(b => b.CreatedAt, DateTime.UtcNow.AddHours(-1))
        );
        var filter = Builders<GraphBatchJob>.Filter.Or(failedFilter, stalePreparingFilter);

        var stuckBatches = await _batchJobs.Find(filter).ToListAsync(ct);
        if (stuckBatches.Count == 0)
        {
            _logger.LogInformation("Cleanup: no failed or stale batches found");
            return;
        }

        var allStuckPageIds = stuckBatches.SelectMany(b => b.PageIds).Distinct().ToList();

        // Release any crawl_state entries still in Processing for these pages
        var released = await _crawlState.DeleteManyAsync(
            Builders<RelationshipCrawlState>.Filter.And(
                Builders<RelationshipCrawlState>.Filter.In(s => s.PageId, allStuckPageIds),
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing)
            ),
            ct
        );

        if (released.DeletedCount > 0)
            _logger.LogInformation(
                "Cleanup: released {Count} orphaned pages from {BatchCount} failed/stale batches",
                released.DeletedCount,
                stuckBatches.Count
            );

        // Delete all failed/stale batch job records so they no longer clutter the dashboard
        var batchIds = stuckBatches.Select(b => b.Id).ToList();
        var deleted = await _batchJobs.DeleteManyAsync(
            Builders<GraphBatchJob>.Filter.In(b => b.Id, batchIds),
            ct
        );

        _logger.LogInformation(
            "Cleanup: removed {Count} failed/stale batch records",
            deleted.DeletedCount
        );
    }

    /// <summary>
    /// Release pages from a failed/expired/cancelled batch so they become eligible for resubmission.
    /// Only deletes crawl_state entries still in Processing — pages that were individually
    /// completed or failed by a prior partial run are left alone.
    /// </summary>
    async Task ReleaseFailedBatchPagesAsync(GraphBatchJob job, CancellationToken ct)
    {
        var result = await _crawlState.DeleteManyAsync(
            Builders<RelationshipCrawlState>.Filter.And(
                Builders<RelationshipCrawlState>.Filter.In(s => s.PageId, job.PageIds),
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing)
            ),
            ct
        );

        if (result.DeletedCount > 0)
            _logger.LogInformation(
                "Batch {BatchId}: released {Count} pages back to unprocessed pool",
                job.Id,
                result.DeletedCount
            );
    }

    /// <summary>
    /// Download and process results from a completed OpenAI batch.
    /// </summary>
    async Task ProcessBatchResultsAsync(GraphBatchJob job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.OutputFileId))
        {
            _logger.LogWarning("Batch {BatchId}: no output file ID", job.Id);
            return;
        }

        job.Status = GraphBatchStatus.Processing;
        await _batchJobs.ReplaceOneAsync(
            Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
            job,
            cancellationToken: ct
        );

        try
        {
            var fileClient = _openAiClient.GetOpenAIFileClient();
            var outputResult = await fileClient.DownloadFileAsync(job.OutputFileId, ct);
            var outputContent = outputResult.Value.ToMemory();
            var outputText = Encoding.UTF8.GetString(outputContent.Span);
            var outputLines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            _logger.LogInformation(
                "Batch {BatchId}: processing {Count} result lines",
                job.Id,
                outputLines.Length
            );

            int completed = 0,
                failed = 0,
                skipped = 0,
                edgesStored = 0;

            foreach (var line in outputLines)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    var outputLine = JsonSerializer.Deserialize<BatchOutputLine>(line, JsonOpts);
                    if (outputLine is null)
                        continue;

                    var customId = outputLine.CustomId;
                    if (!customId.StartsWith("page-"))
                        continue;
                    var pageId = int.Parse(customId["page-".Length..]);

                    // Get page metadata for crawl state
                    var pageDoc = await _pages
                        .Find(new BsonDocument("_id", pageId))
                        .Project(new BsonDocument { { "title", 1 }, { "infobox.Template", 1 }, { "continuity", 1 } })
                        .FirstOrDefaultAsync(ct);

                    var title =
                        pageDoc?.Contains("title") == true
                            ? pageDoc["title"].AsString
                            : $"Page {pageId}";
                    var template =
                        pageDoc?.Contains("infobox") == true
                        && pageDoc["infobox"].AsBsonDocument.Contains("Template")
                            ? RecordService.SanitizeTemplateName(
                                pageDoc["infobox"]["Template"].AsString
                            )
                            : "Unknown";
                    var batchContinuity = pageDoc?.Contains("continuity") == true
                        && Enum.TryParse<Continuity>(pageDoc["continuity"].AsString, true, out var bpc)
                            ? bpc
                            : Continuity.Unknown;

                    // Check for errors in the response
                    if (outputLine.Error is { } batchError)
                    {
                        var errorMsg = batchError.Message;
                        if (string.IsNullOrEmpty(errorMsg))
                            errorMsg = "Unknown error";
                        await _crawlState.ReplaceOneAsync(
                            Builders<RelationshipCrawlState>.Filter.Eq(s => s.PageId, pageId),
                            new RelationshipCrawlState
                            {
                                PageId = pageId,
                                Name = title,
                                Type = template,
                                Continuity = batchContinuity,
                                Status = CrawlStatus.Failed,
                                Error = errorMsg[..Math.Min(errorMsg.Length, 500)],
                                ProcessedAt = DateTime.UtcNow,
                            },
                            new ReplaceOptions { IsUpsert = true },
                            ct
                        );
                        failed++;
                        continue;
                    }

                    // Extract the response text
                    var text = outputLine.Response?.Body?.Choices?.FirstOrDefault()
                        ?.Message?.Content?.Trim();

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await _toolkit.SkipPage(pageId, "Empty response", title, template);
                        skipped++;
                        continue;
                    }

                    // Strip markdown fences if present
                    if (text.StartsWith("```"))
                    {
                        var firstNewline = text.IndexOf('\n');
                        var lastFence = text.LastIndexOf("```");
                        if (firstNewline > 0 && lastFence > firstNewline)
                            text = text[(firstNewline + 1)..lastFence].Trim();
                    }

                    var result = JsonSerializer.Deserialize<RelationshipExtractionResponse>(
                        text,
                        JsonOpts
                    );

                    if (result is null || result.ShouldSkip)
                    {
                        await _toolkit.SkipPage(
                            pageId,
                            result?.SkipReason ?? "No relationships",
                            title,
                            template
                        );
                        skipped++;
                    }
                    else if (result.Edges is { Count: > 0 })
                    {
                        var edgeDtos = result
                            .Edges.Select(e => new EdgeDto
                            {
                                FromId = e.FromId,
                                FromName = e.FromName,
                                FromType = e.FromType,
                                ToId = e.ToId,
                                ToName = e.ToName,
                                ToType = e.ToType,
                                Label = e.Label,
                                ReverseLabel = e.ReverseLabel,
                                Weight = e.Weight,
                                Evidence = e.Evidence,
                                Continuity = e.Continuity,
                            })
                            .ToList();

                        await _toolkit.StoreEdges(pageId, edgeDtos);
                        await _toolkit.MarkProcessed(pageId, edgeDtos.Count, title, template, batchContinuity.ToString());
                        edgesStored += edgeDtos.Count;
                        completed++;
                    }
                    else
                    {
                        await _toolkit.SkipPage(pageId, "No edges extracted", title, template);
                        skipped++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Batch {BatchId}: error processing result line", job.Id);
                    failed++;
                }
            }

            job.Status = GraphBatchStatus.Processed;
            job.CompletedRequests = completed;
            job.FailedRequests = failed;
            job.SkippedRequests = skipped;
            job.TotalEdgesStored = edgesStored;
            job.ProcessedAt = DateTime.UtcNow;

            await _batchJobs.ReplaceOneAsync(
                Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                job,
                cancellationToken: ct
            );

            _logger.LogInformation(
                "Batch {BatchId}: processed. Completed={Completed}, Skipped={Skipped}, Failed={Failed}, Edges={Edges}",
                job.Id,
                completed,
                skipped,
                failed,
                edgesStored
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch {BatchId}: failed to process results", job.Id);
            job.Status = GraphBatchStatus.Failed;
            job.Error = ex.Message[..Math.Min(ex.Message.Length, 500)];
            await _batchJobs.ReplaceOneAsync(
                Builders<GraphBatchJob>.Filter.Eq(b => b.Id, job.Id),
                job
            );
        }
    }

    /// <summary>
    /// Get all batch jobs for the dashboard.
    /// </summary>
    public async Task<List<GraphBatchSummary>> GetBatchJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _batchJobs
            .Find(Builders<GraphBatchJob>.Filter.Empty)
            .SortByDescending(b => b.CreatedAt)
            .Limit(20)
            .ToListAsync(ct);

        return jobs.Select(j => new GraphBatchSummary
            {
                Id = j.Id,
                OpenAiBatchId = j.OpenAiBatchId,
                Status = j.Status.ToString(),
                Model = j.Model,
                TotalRequests = j.TotalRequests,
                CompletedRequests = j.CompletedRequests,
                FailedRequests = j.FailedRequests,
                SkippedRequests = j.SkippedRequests,
                TotalEdgesStored = j.TotalEdgesStored,
                CreatedAt = j.CreatedAt,
                CompletedAt = j.CompletedAt,
                ProcessedAt = j.ProcessedAt,
                Error = j.Error,
            })
            .ToList();
    }

    /// <summary>
    /// Build a single JSONL line for the OpenAI Batch API.
    /// </summary>
    internal string BuildBatchRequestLine(
        string customId,
        string userPrompt,
        JsonElement jsonSchema
    )
    {
        var request = new BatchRequestLine
        {
            CustomId = customId,
            Body = new BatchRequestBody
            {
                Model = _model,
                Messages =
                [
                    new BatchChatMessage { Role = "system", Content = ExtractionSystemPrompt },
                    new BatchChatMessage { Role = "user", Content = userPrompt },
                ],
                ResponseFormat = new BatchResponseFormat
                {
                    JsonSchema = new BatchJsonSchemaRef
                    {
                        Name = "relationship_extraction",
                        Strict = true,
                        Schema = jsonSchema,
                    },
                },
            },
        };

        return JsonSerializer.Serialize(request);
    }

    /// <summary>
    /// Build the JSON schema for RelationshipExtractionResponse for Batch API structured output.
    /// </summary>
    internal static JsonElement BuildJsonSchema()
    {
        var schema = """
            {
                "type": "object",
                "properties": {
                    "shouldSkip": { "type": "boolean" },
                    "skipReason": { "type": ["string", "null"] },
                    "edges": {
                        "type": ["array", "null"],
                        "items": {
                            "type": "object",
                            "properties": {
                                "fromId": { "type": "integer" },
                                "fromName": { "type": "string" },
                                "fromType": { "type": "string" },
                                "toId": { "type": "integer" },
                                "toName": { "type": "string" },
                                "toType": { "type": "string" },
                                "label": { "type": "string" },
                                "reverseLabel": { "type": "string" },
                                "weight": { "type": "number" },
                                "evidence": { "type": "string" },
                                "continuity": { "type": "string" }
                            },
                            "required": ["fromId", "fromName", "fromType", "toId", "toName", "toType", "label", "reverseLabel", "weight", "evidence", "continuity"],
                            "additionalProperties": false
                        }
                    }
                },
                "required": ["shouldSkip", "skipReason", "edges"],
                "additionalProperties": false
            }
            """;

        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    /// <summary>
    /// Get overall progress for the dashboard.
    /// </summary>
    public async Task<GraphBuilderProgress> GetProgressAsync(CancellationToken ct = default)
    {
        // Total pages with infoboxes
        var totalPages = (int)
            await _pages.CountDocumentsAsync(
                new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value)),
                cancellationToken: ct
            );

        // Crawl state aggregation by status
        var statePipeline = new[]
        {
            new BsonDocument(
                "$group",
                new BsonDocument
                {
                    {
                        "_id",
                        new BsonDocument { { "status", "$status" }, { "type", "$type" } }
                    },
                    { "count", new BsonDocument("$sum", 1) },
                }
            ),
        };

        var stateResults = await _crawlState
            .Aggregate<BsonDocument>(statePipeline, cancellationToken: ct)
            .ToListAsync(ct);

        int processedPages = 0,
            skippedPages = 0,
            failedPages = 0;
        var byType = new Dictionary<string, (int total, int processed, int skipped, int failed)>();

        foreach (var doc in stateResults)
        {
            var id = doc["_id"].AsBsonDocument;
            var status = id["status"].AsString;
            var type =
                id.Contains("type") && !id["type"].IsBsonNull ? id["type"].AsString : "Unknown";
            var count = doc["count"].AsInt32;

            if (!byType.ContainsKey(type))
                byType[type] = (0, 0, 0, 0);
            var current = byType[type];

            switch (status)
            {
                case "Completed":
                    processedPages += count;
                    byType[type] = (
                        current.total + count,
                        current.processed + count,
                        current.skipped,
                        current.failed
                    );
                    break;
                case "Skipped":
                    skippedPages += count;
                    byType[type] = (
                        current.total + count,
                        current.processed,
                        current.skipped + count,
                        current.failed
                    );
                    break;
                case "Failed":
                    failedPages += count;
                    byType[type] = (
                        current.total + count,
                        current.processed,
                        current.skipped,
                        current.failed + count
                    );
                    break;
                default:
                    byType[type] = (
                        current.total + count,
                        current.processed,
                        current.skipped,
                        current.failed
                    );
                    break;
            }
        }

        // Total edges
        var totalEdges = await _edges.CountDocumentsAsync(
            Builders<RelationshipEdge>.Filter.Empty,
            cancellationToken: ct
        );

        // Total labels
        var totalLabels = (int)
            await _labels.CountDocumentsAsync(
                Builders<RelationshipLabel>.Filter.Empty,
                cancellationToken: ct
            );

        // All labels sorted by usage (filter out empty labels)
        var allLabels = await _labels
            .Find(
                Builders<RelationshipLabel>.Filter.And(
                    Builders<RelationshipLabel>.Filter.Ne(l => l.Label, ""),
                    Builders<RelationshipLabel>.Filter.Ne(l => l.Label, null)
                )
            )
            .SortByDescending(l => l.UsageCount)
            .ToListAsync(ct);

        // Throughput: calculate from completed batch jobs over the last 24 hours.
        // Batch API processes pages in bulk — individual crawl_state timestamps aren't
        // useful because 1,000 pages flip from Processing→Completed at the same instant.
        var oneDayAgo = DateTime.UtcNow.AddHours(-24);
        var recentBatches = await _batchJobs
            .Find(
                Builders<GraphBatchJob>.Filter.And(
                    Builders<GraphBatchJob>.Filter.Eq(b => b.Status, GraphBatchStatus.Processed),
                    Builders<GraphBatchJob>.Filter.Gte(b => b.ProcessedAt, oneDayAgo)
                )
            )
            .ToListAsync(ct);

        double pagesPerHour = 0;
        if (recentBatches.Count > 0)
        {
            var totalPagesInBatches = recentBatches.Sum(b => b.CompletedRequests + b.SkippedRequests);
            var oldestBatch = recentBatches.Min(b => b.CreatedAt);
            var hoursSpan = (DateTime.UtcNow - oldestBatch).TotalHours;
            if (hoursSpan > 0)
                pagesPerHour = Math.Round(totalPagesInBatches / hoursSpan, 0);
        }

        var pending = totalPages - processedPages - skippedPages - failedPages;
        double? estimatedHoursRemaining =
            pagesPerHour > 0 ? Math.Round(pending / pagesPerHour, 1) : null;

        return new GraphBuilderProgress
        {
            TotalPages = totalPages,
            ProcessedPages = processedPages,
            SkippedPages = skippedPages,
            FailedPages = failedPages,
            PendingPages = pending,
            TotalEdges = totalEdges / 2, // Each relationship has forward+reverse
            TotalLabels = totalLabels,
            PagesPerHour = pagesPerHour,
            EstimatedHoursRemaining = estimatedHoursRemaining,
            ByType = byType
                .Select(kv => new TypeProgress
                {
                    Type = kv.Key,
                    Total = kv.Value.total,
                    Processed = kv.Value.processed,
                    Skipped = kv.Value.skipped,
                    Failed = kv.Value.failed,
                })
                .OrderByDescending(t => t.Total)
                .ToList(),
            RecentLabels = allLabels
                .Select(l => new RecentLabel
                {
                    Label = l.Label,
                    Reverse = l.Reverse,
                    Description = l.Description,
                    UsageCount = l.UsageCount,
                    CreatedAt = l.CreatedAt,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Query the relationship graph using $graphLookup.
    /// </summary>
    public async Task<RelationshipGraphResult> QueryGraphAsync(
        int rootId,
        IReadOnlyList<string> labels,
        int maxDepth = 2,
        Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var edgesCollection = _edges.Database.GetCollection<BsonDocument>("edges");

        // Get root entity info
        var rootPage = await _pages.Find(new BsonDocument("_id", rootId)).FirstOrDefaultAsync(ct);
        var rootName = "";
        if (rootPage != null)
        {
            var ib =
                rootPage.Contains("infobox") && !rootPage["infobox"].IsBsonNull
                    ? rootPage["infobox"].AsBsonDocument
                    : null;
            rootName =
                ib?["Data"].AsBsonArray.OfType<BsonDocument>()
                    .FirstOrDefault(d => d["Label"].AsString == "Titles")
                    ?["Values"].AsBsonArray.FirstOrDefault()
                    ?.AsString ?? (rootPage.Contains("title") ? rootPage["title"].AsString : "");
        }

        var labelFilter =
            labels.Count > 0
                ? new BsonDocument("label", new BsonDocument("$in", new BsonArray(labels)))
                : new BsonDocument();

        var restrictMatch = new BsonDocument();
        if (labels.Count > 0)
            restrictMatch["label"] = new BsonDocument("$in", new BsonArray(labels));

        // Apply continuity filter to edges
        if (continuity.HasValue)
        {
            labelFilter["continuity"] = continuity.Value.ToString();
            restrictMatch["continuity"] = continuity.Value.ToString();
        }

        // maxDepth represents total hops from root.
        // The $match stage already provides 1 hop (root → X).
        // $graphLookup maxDepth N adds N+1 more hops, so we need maxDepth - 2.
        // When maxDepth <= 1, skip $graphLookup entirely (direct edges only).
        BsonDocument[] pipeline = maxDepth <= 1
            ?
            [
                new BsonDocument(
                    "$match",
                    new BsonDocument { { "fromId", rootId } }.AddRange(labelFilter)
                ),
            ]
            :
            [
                new BsonDocument(
                    "$match",
                    new BsonDocument { { "fromId", rootId } }.AddRange(labelFilter)
                ),
                new BsonDocument(
                    "$graphLookup",
                    new BsonDocument
                    {
                        { "from", "edges" },
                        { "startWith", "$toId" },
                        { "connectFromField", "toId" },
                        { "connectToField", "fromId" },
                        { "as", "network" },
                        { "maxDepth", maxDepth - 2 },
                        { "restrictSearchWithMatch", restrictMatch },
                    }
                ),
            ];

        var results = await edgesCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        // Collect unique nodes and edges
        var nodes = new Dictionary<int, RelationshipGraphNode>();
        var edgeSet = new HashSet<(int from, int to, string label)>();

        // Add root
        if (rootPage != null)
        {
            var ib =
                rootPage.Contains("infobox") && !rootPage["infobox"].IsBsonNull
                    ? rootPage["infobox"].AsBsonDocument
                    : null;
            nodes[rootId] = new RelationshipGraphNode
            {
                Id = rootId,
                Name = rootName,
                Type =
                    ib != null && ib.Contains("Template") && !ib["Template"].IsBsonNull
                        ? RecordService.SanitizeTemplateName(ib["Template"].AsString)
                        : "Unknown",
                ImageUrl =
                    ib != null && ib.Contains("ImageUrl") && !ib["ImageUrl"].IsBsonNull
                        ? ib["ImageUrl"].AsString
                        : "",
            };
        }

        foreach (var doc in results)
        {
            // Direct edges from root
            var toId = doc["toId"].AsInt32;
            var toName = doc["toName"].AsString;
            var toType = doc["toType"].AsString;
            var label = doc["label"].AsString;
            var weight = doc.Contains("weight") ? doc["weight"].ToDouble() : 0.5;

            if (!nodes.ContainsKey(toId))
                nodes[toId] = new RelationshipGraphNode
                {
                    Id = toId,
                    Name = toName,
                    Type = toType,
                };

            edgeSet.Add((rootId, toId, label));

            // Network edges from $graphLookup
            if (doc.Contains("network"))
            {
                foreach (var netDoc in doc["network"].AsBsonArray.OfType<BsonDocument>())
                {
                    var nFromId = netDoc["fromId"].AsInt32;
                    var nToId = netDoc["toId"].AsInt32;
                    var nLabel = netDoc["label"].AsString;
                    var nWeight = netDoc.Contains("weight") ? netDoc["weight"].ToDouble() : 0.5;

                    if (!nodes.ContainsKey(nFromId))
                        nodes[nFromId] = new RelationshipGraphNode
                        {
                            Id = nFromId,
                            Name = netDoc["fromName"].AsString,
                            Type = netDoc["fromType"].AsString,
                        };

                    if (!nodes.ContainsKey(nToId))
                        nodes[nToId] = new RelationshipGraphNode
                        {
                            Id = nToId,
                            Name = netDoc["toName"].AsString,
                            Type = netDoc["toType"].AsString,
                        };

                    edgeSet.Add((nFromId, nToId, nLabel));
                }
            }
        }

        return new RelationshipGraphResult
        {
            RootId = rootId,
            RootName = rootName,
            Nodes = nodes.Values.ToList(),
            Edges = edgeSet
                .Select(e => new RelationshipGraphEdge
                {
                    FromId = e.from,
                    ToId = e.to,
                    Label = e.label,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Get distinct labels stored for a specific entity (for query-time LLM to pick relevant ones).
    /// </summary>
    public async Task<List<string>> GetEntityLabelsAsync(
        int pageId,
        Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var edgesCollection = _edges.Database.GetCollection<BsonDocument>("edges");
        var matchFilter = new BsonDocument("fromId", pageId);
        if (continuity.HasValue)
            matchFilter["continuity"] = continuity.Value.ToString();

        var pipeline = new[]
        {
            new BsonDocument("$match", matchFilter),
            new BsonDocument("$group", new BsonDocument("_id", "$label")),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };

        var results = await edgesCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        return results.Select(d => d["_id"].AsString).ToList();
    }

    /// <summary>
    /// Get distinct entity types that have been processed (Completed/Skipped/Failed) in the crawl state.
    /// Used by the Graph Explorer to populate the entity type filter dynamically.
    /// </summary>
    public async Task<List<string>> GetProcessedEntityTypesAsync(CancellationToken ct = default)
    {
        var pipeline = new[]
        {
            new BsonDocument(
                "$match",
                new BsonDocument(
                    "type",
                    new BsonDocument("$nin", new BsonArray { BsonNull.Value, "" })
                )
            ),
            new BsonDocument("$group", new BsonDocument("_id", "$type")),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };

        var results = await _crawlState
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        return results.Select(d => d["_id"].AsString).ToList();
    }

    /// <summary>
    /// Browse processed entities by type with pagination. Only returns entities with status Completed
    /// (i.e. entities that actually have graph data to explore).
    /// </summary>
    public async Task<(List<EntitySearchDto> items, long total)> BrowseEntitiesAsync(
        string type,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var filters = new List<FilterDefinition<RelationshipCrawlState>>
        {
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Completed),
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.Type, type),
        };

        // Filter by continuity using the Pages collection (source of truth),
        // since crawl_state.Continuity is not reliably populated for older documents.
        if (continuity.HasValue)
        {
            var matchingPageIds = await _pages
                .Find(new BsonDocument("continuity", continuity.Value.ToString()))
                .Project(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            var ids = matchingPageIds.Select(d => d["_id"].AsInt32).ToList();
            filters.Add(
                Builders<RelationshipCrawlState>.Filter.In(s => s.PageId, ids)
            );
        }

        if (!string.IsNullOrWhiteSpace(search))
            filters.Add(
                Builders<RelationshipCrawlState>.Filter.Regex(
                    s => s.Name,
                    new BsonRegularExpression(search, "i")
                )
            );

        var filter = Builders<RelationshipCrawlState>.Filter.And(filters);

        var total = await _crawlState.CountDocumentsAsync(filter, cancellationToken: ct);

        var items = await _crawlState
            .Find(filter)
            .SortBy(s => s.Name)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return (
            items.Select(s => new EntitySearchDto { Id = s.PageId, Name = s.Name }).ToList(),
            total
        );
    }

    /// <summary>
    /// Ensure required indexes exist on graph collections.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // edges: $graphLookup traversal
        await _edges.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.FromId),
                    new CreateIndexOptions { Name = "ix_fromId" }
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>
                        .IndexKeys.Ascending(e => e.FromId)
                        .Ascending(e => e.ToId)
                        .Ascending(e => e.Label),
                    new CreateIndexOptions { Name = "ix_fromId_toId_label", Unique = true }
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.SourcePageId),
                    new CreateIndexOptions { Name = "ix_sourcePageId" }
                ),
                new CreateIndexModel<RelationshipEdge>(
                    Builders<RelationshipEdge>.IndexKeys.Ascending(e => e.PairId),
                    new CreateIndexOptions { Name = "ix_pairId" }
                ),
            ],
            ct
        );

        // crawl_state: status + type queries
        await _crawlState.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<RelationshipCrawlState>(
                    Builders<RelationshipCrawlState>
                        .IndexKeys.Ascending(s => s.Status)
                        .Ascending(s => s.Type),
                    new CreateIndexOptions { Name = "ix_status_type" }
                ),
            ],
            ct
        );

        _logger.LogInformation("Relationship graph indexes ensured");
    }
}
