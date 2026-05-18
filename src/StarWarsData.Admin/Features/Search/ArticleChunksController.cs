using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.Admin.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArticleChunksController(ArticleChunkingService chunkingService) : ControllerBase
{
    /// <summary>
    /// Get article chunking progress for the dashboard.
    /// </summary>
    [HttpGet("progress")]
    public async Task<ChunkingProgress> GetProgress(CancellationToken ct)
    {
        return await chunkingService.GetProgressAsync(ct);
    }

    /// <summary>
    /// Purge chunks for pages that are no longer eligible (stale/orphaned),
    /// keeping distinct-chunked counts and the dashboard accurate.
    /// </summary>
    [HttpPost("reconcile-orphans")]
    public async Task<IActionResult> ReconcileOrphans(CancellationToken ct)
    {
        var purged = await chunkingService.ReconcileOrphanedChunksAsync(ct);
        return Ok(new { purgedPages = purged });
    }
}
