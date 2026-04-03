using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RelationshipGraphController(KnowledgeGraphQueryService kg) : ControllerBase
{
    [HttpGet("entity-types")]
    public Task<List<string>> GetEntityTypes(CancellationToken ct) => kg.GetEntityTypesAsync(ct);

    [HttpGet("edge-labels")]
    public Task<List<string>> GetEdgeLabels(CancellationToken ct) => kg.GetEdgeLabelsAsync(ct);

    [HttpGet("browse")]
    public Task<BrowseEntitiesResult> Browse(
        [FromQuery] string? type = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
    ) => kg.BrowseAsync(type, q, page, pageSize, continuity, ct);

    [HttpGet("search")]
    public Task<List<EntitySearchDto>> Search(
        [FromQuery] string q,
        [FromQuery] string? type = null,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
    ) => kg.SearchAsync(q, type, continuity, ct);

    [HttpGet("labels/{pageId:int}")]
    public Task<List<string>> GetLabels(int pageId, CancellationToken ct) =>
        kg.GetLabelsForEntityAsync(pageId, ct);

    [HttpGet("temporal-nodes")]
    public Task<BrowseTemporalNodesResult> BrowseTemporalNodes(
        [FromQuery] string? type = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? continuity = null,
        [FromQuery] bool temporalOnly = false,
        [FromQuery] int? yearFrom = null,
        [FromQuery] int? yearTo = null,
        [FromQuery] string? semantic = null,
        [FromQuery] string? label = null,
        CancellationToken ct = default
    ) =>
        kg.BrowseTemporalNodesAsync(
            type,
            q,
            page,
            pageSize,
            continuity,
            temporalOnly,
            yearFrom,
            yearTo,
            semantic,
            label,
            ct
        );

    [HttpGet("query/{pageId:int}")]
    public Task<RelationshipGraphResult> QueryGraph(
        int pageId,
        [FromQuery] string? labels = null,
        [FromQuery] int maxDepth = 2,
        [FromQuery] string? continuity = null,
        CancellationToken ct = default
    ) => kg.QueryGraphAsync(pageId, labels, maxDepth, continuity, ct);
}
