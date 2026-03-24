using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RelationshipGraphController(
    RelationshipGraphBuilderService graphBuilder
) : ControllerBase
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
    /// Query the persistent relationship graph for an entity.
    /// </summary>
    [HttpGet("query/{pageId:int}")]
    public async Task<RelationshipGraphResult> QueryGraph(
        int pageId,
        [FromQuery] string? labels = null,
        [FromQuery] int maxDepth = 2,
        CancellationToken ct = default)
    {
        var labelList = string.IsNullOrWhiteSpace(labels)
            ? Array.Empty<string>()
            : labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return await graphBuilder.QueryGraphAsync(pageId, labelList, maxDepth, ct);
    }

    /// <summary>
    /// Get available relationship labels for an entity (for query-time LLM to pick).
    /// </summary>
    [HttpGet("labels/{pageId:int}")]
    public async Task<List<string>> GetEntityLabels(int pageId, CancellationToken ct)
    {
        return await graphBuilder.GetEntityLabelsAsync(pageId, ct);
    }
}
