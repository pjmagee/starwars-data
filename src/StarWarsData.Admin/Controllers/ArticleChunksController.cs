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
}
