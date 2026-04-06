using Hangfire;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.Admin.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController(
    ILogger<AdminController> logger,
    PageDownloader pageDownloader,
    OpenAiStatusService aiStatus,
    GalaxyMapETLService galaxyMapETLService,
    InfoboxGraphService infoboxGraphService,
    JobToggleService jobToggleService
) : ControllerBase
{
    // Primary constructor params used directly — no field aliases needed

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
            logger.LogWarning(ex, "Failed to inspect existing jobs; proceeding with enqueue");
        }
        return false;
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
    public async Task<ActionResult<string>> DownloadSinglePage([FromQuery] string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "title query parameter is required" });
        try
        {
            await pageDownloader.DownloadAndSavePageAsync(title, cancellationToken);
            return Ok(new { message = $"Page '{title}' downloaded." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download page {Title}", title);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("download/pages")]
    public ActionResult<string> SyncWikiPages()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(PageDownloader), nameof(PageDownloader.SyncToMongoDbAsync)))
                return Conflict(new { error = "Page download job already running" });
            var jobId = BackgroundJob.Enqueue<PageDownloader>(s => s.SyncToMongoDbAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("templates")]
    public async Task<ActionResult<List<object>>> GetTemplates(CancellationToken cancellationToken)
    {
        var templates = await pageDownloader.GetTemplateCountsAsync(cancellationToken);
        return Ok(templates.Select(t => new { template = t.template, count = t.count }));
    }

    [HttpPost("download/pages/redownload-template")]
    public ActionResult<string> RedownloadByTemplate([FromQuery] string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return BadRequest(new { error = "template query parameter is required (e.g. 'Battle', 'Duel', 'Mission')" });

        var jobId = BackgroundJob.Enqueue<PageDownloader>(s => s.RedownloadByTemplateAsync(template, CancellationToken.None));
        return Ok(
            new
            {
                jobId,
                template,
                message = $"Redownload job started for template matching '{template}'",
            }
        );
    }

    [HttpPost("download/pages/reparse-infoboxes")]
    public ActionResult<string> ReparseInfoboxes([FromQuery] string? template = null)
    {
        var jobId = BackgroundJob.Enqueue<PageDownloader>(s => s.ReparseInfoboxesAsync(template, CancellationToken.None));
        return Ok(
            new
            {
                jobId,
                template = template ?? "(all)",
                message = $"Reparse job started{(template is not null ? $" for template matching '{template}'" : " for all pages with rawInfobox")}",
            }
        );
    }

    [HttpPost("download/pages/incremental")]
    public ActionResult<string> IncrementalSyncPages()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(PageDownloader), nameof(PageDownloader.IncrementalSyncAsync)))
                return Conflict(new { error = "Incremental sync job already running" });
            var jobId = BackgroundJob.Enqueue<PageDownloader>(s => s.IncrementalSyncAsync(CancellationToken.None));
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
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.EnsureIndexesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(ArticleChunkingService), nameof(ArticleChunkingService.ProcessAllAsync)))
                return Conflict(new { error = "Article chunking already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s => s.ProcessAllAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeletePagesCollections)))
                return Conflict(new { error = "Pages deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeletePagesCollections(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeleteTimelineCollections)))
                return Conflict(new { error = "Timeline events deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeleteTimelineCollections(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.CreateTemplateViewsAsync)))
                return Conflict(new { error = "Template views creation already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.CreateTemplateViewsAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// KG-backed timeline rebuild: reads <c>kg.nodes</c> (galactic + real-world temporal
    /// facets) and joins <c>raw.pages</c> for the display-side infobox properties. Emits
    /// rows tagged with <c>Calendar</c> so the Timeline page and the AI agent's
    /// <c>render_timeline</c> tool can filter by calendar mode.
    /// </summary>
    [HttpPost("mongo/create-timeline-events-from-kg")]
    public ActionResult<string> EnqueueCreateTimelineEventsFromKg()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(KgTimelineBuilderService), nameof(KgTimelineBuilderService.BuildAsync)))
                return Conflict(new { error = "KG timeline events job already running" });
            var jobId = BackgroundJob.Enqueue<KgTimelineBuilderService>(s => s.BuildAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(ArticleChunkingService), nameof(ArticleChunkingService.CreateVectorIndexAsync)))
                return Conflict(new { error = "Vector index creation already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s => s.CreateVectorIndexAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(ArticleChunkingService), nameof(ArticleChunkingService.EnsureIndexesAsync)))
                return Conflict(new { error = "Chunk index creation already running" });
            var jobId = BackgroundJob.Enqueue<ArticleChunkingService>(s => s.EnsureIndexesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(CharacterTimelineService), nameof(CharacterTimelineService.GenerateAllTimelinesAsync)))
                return Conflict(new { error = "Character timeline generation already running" });
            var jobId = BackgroundJob.Enqueue<CharacterTimelineService>(s => s.GenerateAllTimelinesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(CharacterTimelineService), nameof(CharacterTimelineService.RefreshTimelineAsync)))
                return Conflict(new { error = "Character timeline refresh already running" });
            var jobId = BackgroundJob.Enqueue<CharacterTimelineService>(s => s.RefreshTimelineAsync(pageId, CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RelationshipGraphBuilderService), nameof(RelationshipGraphBuilderService.SubmitBatchAsync)))
                return Conflict(new { error = "Batch submission already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s => s.SubmitBatchAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RelationshipGraphBuilderService), nameof(RelationshipGraphBuilderService.CheckBatchesAsync)))
                return Conflict(new { error = "Batch check already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s => s.CheckBatchesAsync(CancellationToken.None));
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
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s => s.CleanupFailedBatchesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RelationshipGraphBuilderService), nameof(RelationshipGraphBuilderService.EnsureIndexesAsync)))
                return Conflict(new { error = "Graph index creation already running" });
            var jobId = BackgroundJob.Enqueue<RelationshipGraphBuilderService>(s => s.EnsureIndexesAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/ensure-all-indexes")]
    public ActionResult<string> EnqueueEnsureAllIndexes()
    {
        // Chain all index jobs sequentially via Hangfire continuations
        var pageIndexJob = BackgroundJob.Enqueue<RecordService>(s => s.EnsureIndexesAsync(CancellationToken.None));

        var chunkIndexJob = BackgroundJob.ContinueJobWith<ArticleChunkingService>(pageIndexJob, s => s.EnsureIndexesAsync(CancellationToken.None));

        var vectorIndexJob = BackgroundJob.ContinueJobWith<ArticleChunkingService>(chunkIndexJob, s => s.CreateVectorIndexAsync(CancellationToken.None));

        var graphIndexJob = BackgroundJob.ContinueJobWith<RelationshipGraphBuilderService>(vectorIndexJob, s => s.EnsureIndexesAsync(CancellationToken.None));

        return Ok(
            new
            {
                jobs = new
                {
                    pageIndexJob,
                    chunkIndexJob,
                    vectorIndexJob,
                    graphIndexJob,
                },
                message = "All index jobs queued (pages → chunks → vector search → KG graph)",
            }
        );
    }

    [HttpGet("graph/priority-categories")]
    public ActionResult<string[]> GetPriorityCategories() => Ok(RelationshipGraphBuilderService.GetPriorityCategories());

    [HttpPost("graph/priority-categories")]
    public ActionResult SetPriorityCategories([FromBody] string[] categories)
    {
        RelationshipGraphBuilderService.SetPriorityCategories(categories);
        logger.LogInformation("Graph builder priority categories set: {Categories}", string.Join(", ", categories));
        return Ok(new { message = $"Priority set: {string.Join(", ", categories)}" });
    }

    // === Infobox Knowledge Graph ===

    [HttpPost("mongo/build-infobox-graph")]
    public async Task<ActionResult<string>> BuildInfoboxGraph(CancellationToken ct)
    {
        try
        {
            await infoboxGraphService.BuildGraphAsync(ct);
            return Ok(new { message = "Infobox knowledge graph built successfully." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build infobox graph");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // === Territory Control ===

    [HttpPost("mongo/build-galaxy-map")]
    public async Task<ActionResult<string>> BuildGalaxyMap(CancellationToken ct)
    {
        try
        {
            await galaxyMapETLService.BuildGalaxyMapAsync(ct);
            return Ok(new { message = "Unified galaxy map built (territory + events)." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build galaxy map");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // === Ask page suggestions ===

    [HttpPost("mongo/refresh-ask-suggestions")]
    public ActionResult<string> EnqueueRefreshAskSuggestions()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(StarWarsData.Services.Suggestions.SuggestionAgentService), nameof(StarWarsData.Services.Suggestions.SuggestionAgentService.GenerateAsync)))
                return Conflict(new { error = "Ask suggestions refresh already running" });
            var jobId = BackgroundJob.Enqueue<StarWarsData.Services.Suggestions.SuggestionAgentService>(s => s.GenerateAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // === OpenAI Status ===

    [HttpGet("openai/status")]
    public OpenAiHealthReport GetOpenAiStatus() => aiStatus.GetHealthReport();

    // === Job Toggles ===

    [HttpGet("jobs")]
    public async Task<List<StarWarsData.Models.Entities.JobToggle>> GetJobToggles(CancellationToken ct) => await jobToggleService.GetAllAsync(ct);

    [HttpPost("jobs/{jobId}/toggle")]
    public async Task<ActionResult> ToggleJob(string jobId, [FromQuery] bool enabled, CancellationToken ct)
    {
        await jobToggleService.SetEnabledAsync(jobId, enabled, ct);
        logger.LogInformation("Job {JobId} {State}", jobId, enabled ? "enabled" : "disabled");
        return Ok(new { jobId, enabled });
    }
}
