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
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    ) => kg.BrowseAsync(type, q, page, pageSize, continuity, universe, ct);

    [HttpGet("search")]
    public Task<List<EntitySearchDto>> Search(
        [FromQuery] string q,
        [FromQuery] string? type = null,
        [FromQuery] string? continuity = null,
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    ) => kg.SearchAsync(q, type, continuity, universe, ct);

    [HttpGet("labels/{pageId:int}")]
    public Task<EntityLabelsResult> GetLabels(int pageId, CancellationToken ct) => kg.GetLabelsForEntityAsync(pageId, ct);

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
        [FromQuery] string? calendar = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    ) => kg.BrowseTemporalNodesAsync(type, q, page, pageSize, continuity, temporalOnly, yearFrom, yearTo, semantic, label, calendar, sortBy, sortDirection, universe, ct);

    [HttpGet("edge-label-stats")]
    public Task<BrowseEdgeLabelsResult> BrowseEdgeLabels(
        [FromQuery] string? q = null,
        [FromQuery] string? continuity = null,
        [FromQuery] string? fromType = null,
        [FromQuery] string? toType = null,
        [FromQuery] long minCount = 0,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    ) => kg.BrowseEdgeLabelsAsync(q, continuity, fromType, toType, minCount, page, pageSize, sortBy, sortDirection, universe, ct);

    [HttpGet("query/{pageId:int}")]
    public Task<RelationshipGraphResult> QueryGraph(
        int pageId,
        [FromQuery] string? labels = null,
        [FromQuery] int maxDepth = 2,
        [FromQuery] string? continuity = null,
        [FromQuery] bool onlyRoot = false,
        [FromQuery] string? universe = null,
        [FromQuery] int? yearFrom = null,
        [FromQuery] int? yearTo = null,
        CancellationToken ct = default
    ) => kg.QueryGraphAsync(pageId, labels, maxDepth, continuity, onlyRoot, universe, yearFrom, yearTo, ct);
}
