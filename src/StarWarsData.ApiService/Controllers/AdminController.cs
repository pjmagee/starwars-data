using Hangfire;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    readonly ILogger<AdminController> _logger;
    readonly InfoboxDownloader _infoboxDownloader;
    readonly InfoboxRelationshipProcessor _infoboxRelationshipProcessor;
    readonly PageDownloader _pageDownloader;
    readonly RecordService _recordService;

    public AdminController(
        ILogger<AdminController> logger,
        InfoboxDownloader infoboxDownloader,
        InfoboxRelationshipProcessor infoboxRelationshipProcessor,
        PageDownloader pageDownloader,
        RecordService recordService
    )
    {
        _logger = logger;
        _infoboxDownloader = infoboxDownloader;
        _infoboxRelationshipProcessor = infoboxRelationshipProcessor;
        _pageDownloader = pageDownloader;
        _recordService = recordService;
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

    [HttpPost("infobox/download")]
    public ActionResult<Guid> EnqueueInfoboxDownload()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _infoboxDownloader.DownloadInfoboxesAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("infobox/relationships")]
    public ActionResult<Guid> EnqueueInfoboxRelationships()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _infoboxRelationshipProcessor.CreateRelationshipsAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-embeddings")]
    public ActionResult<Guid> EnqueueCreateEmbeddings()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.ProcessEmbeddingsAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-embeddings")]
    public ActionResult<Guid> EnqueueDeleteEmbeddings()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.DeleteOpenAiEmbeddingsAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-collections")]
    public ActionResult<Guid> EnqueueDeleteCollections()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.DeleteCollections(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-timeline-events")]
    [Obsolete("Use create-categorized-timeline-events instead")]
    public ActionResult<Guid> EnqueueCreateTimelineEvents()
    {
        try
        {
            // Redirect to the new categorized method
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.CreateCategorizedTimelineEvents(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-categorized-timeline-events")]
    public ActionResult<Guid> EnqueueCreateCategorizedTimelineEvents()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.CreateCategorizedTimelineEvents(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-index-embeddings")]
    public ActionResult<Guid> EnqueueCreateIndexEmbeddings()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.CreateVectorIndexesAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/delete-index-embeddings")]
    public ActionResult<Guid> EnqueueDeleteIndexEmbeddings()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.DeleteVectorIndexesAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/add-character-relationships")]
    public ActionResult<Guid> EnqueueAddCharacterRelationships()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _recordService.AddCharacterRelationshipsAsync()
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("wookieepedia/sync")]
    public ActionResult<Guid> EnqueueDownloadPages()
    {
        try
        {
            var jobId = BackgroundJob.Enqueue(() =>
                _pageDownloader.SyncToMongoDbAsync(CancellationToken.None)
            );
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
