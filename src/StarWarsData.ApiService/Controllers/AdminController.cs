using Hangfire;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController(
    ILogger<AdminController> logger,
    PageDownloader pageDownloader,
    RecordService recordService,
    CharacterTimelineService characterTimelineService,
    RelationshipGraphBuilderService graphBuilder,
    ArticleChunkingService articleChunkingService,
    OpenAiStatusService aiStatus,
    TerritoryInferenceService territoryInferenceService
) : ControllerBase
{
    readonly ILogger<AdminController> _logger = logger;
    readonly PageDownloader _pageDownloader = pageDownloader;
    readonly RecordService _recordService = recordService;
    readonly CharacterTimelineService _characterTimelineService = characterTimelineService;
    readonly RelationshipGraphBuilderService _graphBuilder = graphBuilder;
    readonly ArticleChunkingService _articleChunkingService = articleChunkingService;
    readonly OpenAiStatusService _aiStatus = aiStatus;
    readonly TerritoryInferenceService _territoryInferenceService = territoryInferenceService;

    bool IsJobAlreadyActive(Type type, string methodName)
    {
        try
        {
            var monitoring = JobStorage.Current.GetMonitoringApi();
            foreach (var q in monitoring.Queues())
            {
                var processing = monitoring.ProcessingJobs(0, 100);
                foreach (var kv in processing)
                {
                    var job = kv.Value.Job;
                    if (job?.Type == type && job.Method?.Name == methodName)
                        return true;
                }

                var enqueued = monitoring.EnqueuedJobs(q.Name, 0, 100);
                foreach (var kv in enqueued)
                {
                    var job = kv.Value.Job;
                    if (job?.Type == type && job.Method?.Name == methodName)
                        return true;
                }
            }

            var scheduled = monitoring.ScheduledJobs(0, 100);
            foreach (var kv in scheduled)
            {
                var job = kv.Value.Job;
                if (job?.Type == type && job.Method?.Name == methodName)
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect existing jobs; proceeding with enqueue");
        }
        return false;
    }

    [HttpGet("jobs")]
    public ActionResult GetAllJobs()
    {
        return Redirect("/hangfire");
    }

    [HttpGet("jobs/{id:guid}")]
    public ActionResult GetJob(Guid id)
    {
        return Redirect($"/hangfire/jobs/details/{id}");
    }

    [HttpDelete("jobs/{id:guid}")]
    public IActionResult CancelJob(Guid id)
    {
        try
        {
            BackgroundJob.Delete(id.ToString());
            return NoContent();
        }
        catch
        {
            return NotFound();
        }
    }

    [HttpPost("download/page")]
    public async Task<ActionResult<string>> DownloadSinglePage(
        [FromQuery] string title,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "title query parameter is required" });
        try
        {
            await _pageDownloader.DownloadAndSavePageAsync(title, cancellationToken);
            return Ok(new { message = $"Page '{title}' downloaded." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download page {Title}", title);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("download/pages")]
    public ActionResult<string> SyncWikiPages()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(PageDownloader),
                    nameof(PageDownloader.SyncToMongoDbAsync)
                )
            )
                return Conflict(new { error = "Page download job already running" });
            var jobId = BackgroundJob.Enqueue<PageDownloader>(s =>
                s.SyncToMongoDbAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("download/pages/incremental")]
    public ActionResult<string> IncrementalSyncPages()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(PageDownloader),
                    nameof(PageDownloader.IncrementalSyncAsync)
                )
            )
                return Conflict(new { error = "Incremental sync job already running" });
            var jobId = BackgroundJob.Enqueue<PageDownloader>(s =>
                s.IncrementalSyncAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/ensure-indexes")]
    public ActionResult<string> EnqueueEnsureIndexes()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.EnsureIndexesAsync)))
                return Conflict(new { error = "Index creation already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.EnsureIndexesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-embeddings")]
    public ActionResult<string> EnqueueCreateEmbeddings()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(ArticleChunkingService),
                    nameof(ArticleChunkingService.ProcessAllAsync)
                )
            )
                return Conflict(new { error = "Article chunking already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s =>
                s.ProcessAllAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-embeddings")]
    public ActionResult<string> EnqueueDeleteEmbeddings()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.DeleteOpenAiEmbeddingsAsync)
                )
            )
                return Conflict(new { error = "Embeddings deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.DeleteOpenAiEmbeddingsAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-pages")]
    public ActionResult<string> EnqueueDeletePages()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.DeletePagesCollections)
                )
            )
                return Conflict(new { error = "Pages deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.DeletePagesCollections(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-timeline-events")]
    public ActionResult<string> EnqueueDeleteTimelineEvents()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.DeleteTimelineCollections)
                )
            )
                return Conflict(new { error = "Timeline events deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.DeleteTimelineCollections(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-template-views")]
    public ActionResult<string> EnqueueCreateTemplateViews()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.CreateTemplateViewsAsync)
                )
            )
                return Conflict(new { error = "Template views creation already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.CreateTemplateViewsAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-categorized-timeline-events")]
    public ActionResult<string> EnqueueCreateCategorizedTimelineEvents()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.CreateCategorizedTimelineEvents)
                )
            )
                return Conflict(new { error = "Categorized timeline events job already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.CreateCategorizedTimelineEvents(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-index-embeddings")]
    public ActionResult<string> EnqueueCreateVectorIndexes()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(ArticleChunkingService),
                    nameof(ArticleChunkingService.CreateVectorIndexAsync)
                )
            )
                return Conflict(new { error = "Vector index creation already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s =>
                s.CreateVectorIndexAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/ensure-chunk-indexes")]
    public ActionResult<string> EnqueueEnsureChunkIndexes()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(ArticleChunkingService),
                    nameof(ArticleChunkingService.EnsureIndexesAsync)
                )
            )
                return Conflict(new { error = "Chunk index creation already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s =>
                s.EnsureIndexesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-index-embeddings")]
    public ActionResult<string> EnqueueDeleteVectorIndexes()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RecordService),
                    nameof(RecordService.DeleteVectorIndexesAsync)
                )
            )
                return Conflict(new { error = "Vector index deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s =>
                s.DeleteVectorIndexesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-character-timelines")]
    public ActionResult<string> EnqueueCreateCharacterTimelines()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(CharacterTimelineService),
                    nameof(CharacterTimelineService.GenerateAllTimelinesAsync)
                )
            )
                return Conflict(new { error = "Character timeline generation already running" });
            var jobId = BackgroundJob.Enqueue<CharacterTimelineService>(s =>
                s.GenerateAllTimelinesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/refresh-character-timeline")]
    public ActionResult<string> EnqueueRefreshCharacterTimeline([FromQuery] int pageId)
    {
        if (pageId <= 0)
            return BadRequest(new { error = "pageId query parameter is required" });
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(CharacterTimelineService),
                    nameof(CharacterTimelineService.RefreshTimelineAsync)
                )
            )
                return Conflict(new { error = "Character timeline refresh already running" });
            var jobId = BackgroundJob.Enqueue<CharacterTimelineService>(s =>
                s.RefreshTimelineAsync(pageId, CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/build-relationship-graph")]
    public ActionResult<string> EnqueueBuildRelationshipGraph()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RelationshipGraphBuilderService),
                    nameof(RelationshipGraphBuilderService.ProcessAllAsync)
                )
            )
                return Conflict(new { error = "Relationship graph builder already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s =>
                s.ProcessAllAsync(100, CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/submit-graph-batch")]
    public ActionResult<string> EnqueueSubmitGraphBatch()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RelationshipGraphBuilderService),
                    nameof(RelationshipGraphBuilderService.SubmitBatchAsync)
                )
            )
                return Conflict(new { error = "Batch submission already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s =>
                s.SubmitBatchAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/check-graph-batches")]
    public ActionResult<string> EnqueueCheckGraphBatches()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RelationshipGraphBuilderService),
                    nameof(RelationshipGraphBuilderService.CheckBatchesAsync)
                )
            )
                return Conflict(new { error = "Batch check already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s =>
                s.CheckBatchesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/cleanup-graph-batches")]
    public ActionResult<string> EnqueueCleanupGraphBatches()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s =>
                s.CleanupFailedBatchesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/ensure-graph-indexes")]
    public ActionResult<string> EnqueueEnsureGraphIndexes()
    {
        try
        {
            if (
                IsJobAlreadyActive(
                    typeof(RelationshipGraphBuilderService),
                    nameof(RelationshipGraphBuilderService.EnsureIndexesAsync)
                )
            )
                return Conflict(new { error = "Graph index creation already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s =>
                s.EnsureIndexesAsync(CancellationToken.None)
            );
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("graph/priority-categories")]
    public ActionResult<string[]> GetPriorityCategories()
        => Ok(RelationshipGraphBuilderService.GetPriorityCategories());

    [HttpPost("graph/priority-categories")]
    public ActionResult SetPriorityCategories([FromBody] string[] categories)
    {
        RelationshipGraphBuilderService.SetPriorityCategories(categories);
        logger.LogInformation("Graph builder priority categories set: {Categories}", string.Join(", ", categories));
        return Ok(new { message = $"Priority set: {string.Join(", ", categories)}" });
    }

    // === Territory Control ===

    [HttpPost("mongo/infer-territory-control")]
    public async Task<ActionResult<string>> InferTerritoryControl(CancellationToken ct)
    {
        try
        {
            await _territoryInferenceService.InferTerritoryAsync(ct);
            return Ok(new { message = "Territory control inferred from battle outcomes and government events." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to infer territory control");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // === OpenAI Status ===

    [HttpGet("openai/status")]
    public OpenAiHealthReport GetOpenAiStatus() => _aiStatus.GetHealthReport();
}
