using Hangfire;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;
using StarWarsData.Services.AI.Agents;
using StarWarsData.Services.AI.Agents.CharacterTimelines;

namespace StarWarsData.Admin.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController(
    ILogger<AdminController> logger,
    PageDownloader pageDownloader,
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

    [HttpGet("wiki-sync/recent")]
    public async Task<StarWarsData.Models.Entities.RecentSyncStatus> GetRecentWikiSync([FromQuery] int limit = 50, CancellationToken ct = default) =>
        await pageDownloader.GetRecentSyncStatusAsync(limit, ct);

    // === Prod → Dev raw data refresh (Design-033) ===

    /// <summary>
    /// Full-control prod→dev refresh. Pulls <c>raw.pages</c> backward from
    /// <c>starwars-prod</c> into the dev database via a server-side <c>$merge</c> (no
    /// document transits this process). Read-only against prod; the service refuses to
    /// run if the write target is <c>starwars-prod</c>.
    /// <para>
    /// <c>?since=N</c> ⇒ recent slice (pages changed in the last N days). <c>since</c>
    /// omitted ⇒ full mirror. <c>?wipe=true</c> empties dev's <c>raw.pages</c> first for
    /// a true mirror (removes upstream-deleted pages). Endpoint-only — the full-mirror /
    /// wipe path is deliberately kept off the one-click dashboard surface (Design-033
    /// Open Question 2). Rebuild dev's derived data afterward via Phase 5 → 3a → 4a.
    /// </para>
    /// </summary>
    [HttpPost("sync/prod-to-dev")]
    public IActionResult RefreshFromProd([FromQuery] int? since, [FromQuery] bool wipe = false) => EnqueueProdToDevRefresh(since, wipe);

    /// <summary>
    /// Dashboard one-click: the 14-day recent slice (the QA case). A dedicated clean
    /// route — Aspire's <c>WithHttpCommand</c> treats the whole command path literally
    /// and URL-encodes a <c>?</c>, so a query string can't be carried by the Aspire
    /// command. Full mirror / custom window / wipe stay on the query-param route above.
    /// </summary>
    [HttpPost("sync/prod-to-dev/recent")]
    public IActionResult RefreshFromProdRecent() => EnqueueProdToDevRefresh(14, wipe: false);

    IActionResult EnqueueProdToDevRefresh(int? since, bool wipe)
    {
        try
        {
            if (IsJobAlreadyActive(typeof(ProdToDevSyncService), nameof(ProdToDevSyncService.RefreshRawPagesAsync)))
                return Conflict(new { error = "A prod→dev refresh is already running." });

            var jobId = BackgroundJob.Enqueue<ProdToDevSyncService>(s => s.RefreshRawPagesAsync(since, wipe, CancellationToken.None));
            return Accepted(
                new
                {
                    jobId,
                    mode = since is null ? "full" : $"since-{since}d",
                    wipe,
                }
            );
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

    [HttpPost("mongo/ensure-all-indexes")]
    public ActionResult<string> EnqueueEnsureAllIndexes()
    {
        // Chain all index jobs sequentially via Hangfire continuations
        var pageIndexJob = BackgroundJob.Enqueue<RecordService>(s => s.EnsureIndexesAsync(CancellationToken.None));

        var chunkIndexJob = BackgroundJob.ContinueJobWith<ArticleChunkingService>(pageIndexJob, s => s.EnsureIndexesAsync(CancellationToken.None));

        var vectorIndexJob = BackgroundJob.ContinueJobWith<ArticleChunkingService>(chunkIndexJob, s => s.CreateVectorIndexAsync(CancellationToken.None));

        return Ok(
            new
            {
                jobs = new
                {
                    pageIndexJob,
                    chunkIndexJob,
                    vectorIndexJob,
                },
                message = "All index jobs queued (pages → chunks → vector search)",
            }
        );
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
            if (IsJobAlreadyActive(typeof(StarWarsData.Services.AI.Agents.SuggestionAgent), nameof(StarWarsData.Services.AI.Agents.SuggestionAgent.GenerateAsync)))
                return Conflict(new { error = "Ask suggestions refresh already running" });
            var jobId = BackgroundJob.Enqueue<StarWarsData.Services.AI.Agents.SuggestionAgent>(s => s.GenerateAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("openai/sync-spend")]
    public ActionResult<string> EnqueueOpenAiSpendSync()
    {
        try
        {
            // Clear stale scheduled retries from prior failed attempts. Hangfire's default
            // AutomaticRetry queues 10 retries on exception, and the nightly cron means a
            // missed run is automatically retried within 24h — so accumulated retries are
            // just noise that would block the conflict guard below. Only true active work
            // (Processing/Enqueued) blocks a manual re-trigger.
            DeleteScheduledJobs(typeof(OpenAiSpendSyncService), nameof(OpenAiSpendSyncService.SyncAsync));
            if (IsJobAlreadyProcessing(typeof(OpenAiSpendSyncService), nameof(OpenAiSpendSyncService.SyncAsync)))
                return Conflict(new { error = "OpenAI spend sync already running" });
            var jobId = BackgroundJob.Enqueue<OpenAiSpendSyncService>(s => s.SyncAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    void DeleteScheduledJobs(Type type, string methodName)
    {
        try
        {
            var monitoring = JobStorage.Current.GetMonitoringApi();
            foreach (var (id, scheduled) in monitoring.ScheduledJobs(0, 100))
            {
                var job = scheduled.Job;
                if (job?.Type == type && job.Method?.Name == methodName)
                    BackgroundJob.Delete(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete stale scheduled jobs for {Type}.{Method}", type.Name, methodName);
        }
    }

    bool IsJobAlreadyProcessing(Type type, string methodName)
    {
        try
        {
            var monitoring = JobStorage.Current.GetMonitoringApi();
            foreach (var q in monitoring.Queues())
            {
                foreach (var kv in monitoring.ProcessingJobs(0, 100))
                    if (kv.Value.Job?.Type == type && kv.Value.Job.Method?.Name == methodName)
                        return true;
                foreach (var kv in monitoring.EnqueuedJobs(q.Name, 0, 100))
                    if (kv.Value.Job?.Type == type && kv.Value.Job.Method?.Name == methodName)
                        return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to inspect existing jobs; proceeding with enqueue");
        }
        return false;
    }

    // === Holocron (Phase 2 KG enrichment agent) ===

    [HttpPost("holocron/run-daily-pass")]
    public ActionResult<string> EnqueueHolocronDailyPass()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(StarWarsData.Services.AI.Agents.HolocronAgent), nameof(StarWarsData.Services.AI.Agents.HolocronAgent.RunDailyPassAsync)))
                return Conflict(new { error = "Holocron daily pass already running" });
            var jobId = BackgroundJob.Enqueue<StarWarsData.Services.AI.Agents.HolocronAgent>(a => a.RunDailyPassAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("holocron/enhance/{pageId:int}")]
    public async Task<ActionResult<NodeEnhancementSummary>> EnhanceNode(int pageId, [FromServices] StarWarsData.Services.AI.Agents.HolocronAgent holocron, CancellationToken ct)
    {
        var summary = await holocron.EnhanceNodeAsync(pageId, "manual", ct);
        return Ok(summary);
    }

    [HttpPost("holocron/staleness-sweep")]
    public async Task<ActionResult<StalenessSweepSummary>> RunStalenessSweep([FromServices] StarWarsData.Services.AI.Agents.HolocronAgent holocron, CancellationToken ct)
    {
        var summary = await holocron.RunStalenessSweepAsync(ct);
        return Ok(summary);
    }

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
