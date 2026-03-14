using Hangfire;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;
using Hangfire.Storage;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{    readonly ILogger<AdminController> _logger;
    readonly InfoboxExtractor _infoboxExtractor;
    readonly InfoboxRelationshipProcessor _infoboxRelationshipProcessor;
    readonly PageDownloader _pageDownloader;
    readonly RecordService _recordService;

    public AdminController(
        ILogger<AdminController> logger,
        InfoboxExtractor infoboxExtractor,
        InfoboxRelationshipProcessor infoboxRelationshipProcessor,
        PageDownloader pageDownloader,
        RecordService recordService
    )
    {
        _logger = logger;
        _infoboxExtractor = infoboxExtractor;
        _infoboxRelationshipProcessor = infoboxRelationshipProcessor;
        _pageDownloader = pageDownloader;
        _recordService = recordService;
    }

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
        // Redirect to Hangfire dashboard for job management
        return Redirect("/hangfire");
    }

    [HttpGet("jobs/{id:guid}")]
    public ActionResult GetJob(Guid id)
    {
        // Redirect to Hangfire dashboard for specific job
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

    [HttpPost("process/infobox-relationships")]
    public ActionResult<string> ProcessInfoboxRelationships()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(InfoboxRelationshipProcessor), nameof(InfoboxRelationshipProcessor.CreateRelationshipsAsync)))
                return Conflict(new { error = "Infobox relationship processing already running" });
            var jobId = BackgroundJob.Enqueue<InfoboxRelationshipProcessor>(s => s.CreateRelationshipsAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.ProcessEmbeddingsAsync)))
                return Conflict(new { error = "Embeddings creation already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.ProcessEmbeddingsAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeleteOpenAiEmbeddingsAsync)))
                return Conflict(new { error = "Embeddings deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeleteOpenAiEmbeddingsAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-infobox-collections")]
    public ActionResult<string> EnqueueDeleteCollections()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeleteInfoboxCollections)))
                return Conflict(new { error = "Infobox collections deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeleteInfoboxCollections(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-page-infobox-collections")]
    public ActionResult<string> EnqueueDeletePageInfoboxCollections()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeletePageInfoboxCollections)))
                return Conflict(new { error = "Extracted infobox collections deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeletePageInfoboxCollections(CancellationToken.None));
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

    [HttpPost("mongo/add-character-relationships")]
    public ActionResult<string> EnqueueAddCharacterRelationships()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.AddCharacterRelationshipsAsync)))
                return Conflict(new { error = "Character relationships job already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.AddCharacterRelationshipsAsync());
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("extract/infoboxes")]
    public ActionResult<string> ExtractInfoboxes()
    {
        try
        {
            if (IsJobAlreadyActive(typeof(InfoboxExtractor), nameof(InfoboxExtractor.ExtractInfoboxesAsync)))
                return Conflict(new { error = "Infobox extraction already running" });
            var jobId = BackgroundJob.Enqueue<InfoboxExtractor>(s => s.ExtractInfoboxesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.CreateCategorizedTimelineEvents)))
                return Conflict(new { error = "Categorized timeline events job already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.CreateCategorizedTimelineEvents(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.CreateVectorIndexesAsync)))
                return Conflict(new { error = "Vector index creation already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.CreateVectorIndexesAsync(CancellationToken.None));
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
            if (IsJobAlreadyActive(typeof(RecordService), nameof(RecordService.DeleteVectorIndexesAsync)))
                return Conflict(new { error = "Vector index deletion already running" });
            var jobId = BackgroundJob.Enqueue<RecordService>(s => s.DeleteVectorIndexesAsync(CancellationToken.None));
            return Ok(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
