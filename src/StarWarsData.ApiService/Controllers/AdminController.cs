using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.ApiService.Jobs;
using StarWarsData.Models.Entities;
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

    readonly IBackgroundJobQueue _jobQueue;
    readonly IMongoCollection<JobInfo> _jobCollection;

    public AdminController(
        ILogger<AdminController> logger,
        InfoboxDownloader infoboxDownloader,
        InfoboxRelationshipProcessor infoboxRelationshipProcessor,
        PageDownloader pageDownloader,
        RecordService recordService,
        IBackgroundJobQueue jobQueue,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient)
    {
        _logger = logger;
        _infoboxDownloader = infoboxDownloader;
        _infoboxRelationshipProcessor = infoboxRelationshipProcessor;
        _pageDownloader = pageDownloader;
        _recordService = recordService;
        _jobQueue = jobQueue;
        var db = mongoClient.GetDatabase(settingsOptions.Value.RawDb);
        _jobCollection = db.GetCollection<JobInfo>("jobs");
    }

    [HttpGet("jobs")]
    public async Task<ActionResult<IEnumerable<JobInfo>>> GetAllJobs()
    {
        try
        {
            var jobs = await _jobCollection.Find(FilterDefinition<JobInfo>.Empty).ToListAsync();
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching all jobs");
            return StatusCode(500, new
                {
                    error = ex.Message
                }
            );
        }
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<ActionResult<JobInfo>> GetJob(Guid id)
    {
        var job = await _jobCollection.Find(j => j.Id == id).FirstOrDefaultAsync();
        if (job == null) return NotFound();
        return Ok(job);
    }

    [HttpDelete("jobs/{id:guid}")]
    public IActionResult CancelJob(Guid id)
    {
        if (_jobQueue.TryCancel(id))
            return NoContent();

        return NotFound();
    }

    [HttpPost("infobox/download")]
    public ActionResult<Guid> EnqueueInfoboxDownload()
    {
        try
        {
            var jobId = _jobQueue.Enqueue(ct => _infoboxDownloader.ExecuteAsync(ct), "InfoboxDownload");
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
            var jobId = _jobQueue.Enqueue(ct => _infoboxRelationshipProcessor.ExecuteAsync(ct), "InfoboxRelationships");
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
            var jobId = _jobQueue.Enqueue(ct => _recordService.ProcessEmbeddingsAsync(ct), "CreateEmbeddings");
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
            var jobId = _jobQueue.Enqueue(ct => _recordService.DeleteOpenAiEmbeddingsAsync(ct), "DeleteEmbeddings");
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/populate-database")]
    public ActionResult<Guid> EnqueuePopulateDatabase()
    {
        try
        {
            var jobId = _jobQueue.Enqueue(ct => _recordService.PopulateAsync(ct), "PopulateDatabase");
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
            var jobId = _jobQueue.Enqueue(ct => _recordService.DeleteCollections(ct), "DeleteCollections");
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("mongo/create-timeline-events")]
    public ActionResult<Guid> EnqueueCreateTimelineEvents()
    {
        try
        {
            var jobId = _jobQueue.Enqueue(ct => _recordService.CreateTimelineEvents(ct), "CreateTimelineEvents");
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
            var jobId = _jobQueue.Enqueue(ct => _recordService.CreateVectorIndexesAsync(ct), "CreateIndexEmbeddings");
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
            var jobId = _jobQueue.Enqueue(ct => _recordService.DeleteVectorIndexesAsync(ct), "DeleteIndexEmbeddings");
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
            var jobId = _jobQueue.Enqueue(_ => _recordService.AddCharacterRelationshipsAsync(), "AddCharacterRelationships");
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("wookieepedia/sync")]
    public ActionResult<Guid> EnqueueSyncWookieepediaToMongoDb()
    {
        try
        {
            _logger.LogInformation("Received request to sync Wookieepedia to MongoDB");
            var jobId = _jobQueue.Enqueue(async ct => { await _pageDownloader.SyncToMongoDbAsync(ct); }, "SyncWookieepediaToMongoDB");
            _logger.LogInformation("Enqueued SyncWookieepediaToMongoDB job with ID {JobId}", jobId);
            return Ok(jobId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
