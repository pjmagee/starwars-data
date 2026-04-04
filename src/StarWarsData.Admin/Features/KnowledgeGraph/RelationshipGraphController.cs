using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.Admin.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RelationshipGraphController(RelationshipGraphBuilderService graphBuilder)
    : ControllerBase
{
    /// <summary>
    /// Get the graph builder crawl progress for the dashboard.
    /// </summary>
    [HttpGet("progress")]
    public async Task<GraphBuilderProgress> GetProgress(CancellationToken ct)
    {
        return await graphBuilder.GetProgressAsync(ct);
    }

    /// <summary>
    /// Get recent batch jobs for the dashboard.
    /// </summary>
    [HttpGet("batches")]
    public async Task<List<GraphBatchSummary>> GetBatches(CancellationToken ct)
    {
        return await graphBuilder.GetBatchJobsAsync(ct);
    }
}
