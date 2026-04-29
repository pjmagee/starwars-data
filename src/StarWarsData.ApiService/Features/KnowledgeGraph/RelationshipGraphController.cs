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
    public Task<EntityLabelsResult> GetLabels(
        int pageId,
        [FromQuery] string? continuity = null,
        // Accept both `realm` (canonical, what GlobalFilterService.GetRealmQueryParam emits)
        // and `universe` (legacy alias). The other endpoints in this controller still bind
        // `universe`; new clients should send `realm=` and we coalesce here so both work.
        [FromQuery] string? realm = null,
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    ) => kg.GetLabelsForEntityAsync(pageId, continuity, realm ?? universe, ct);

    /// <summary>
    /// Per-edge rows for a node, projected from the node's perspective with Phase 2 annotation
    /// context inline. Backs the virtualised relationships table on the Knowledge Graph
    /// node-detail panel — a single fetch returns up to <c>limit</c> rows and the table
    /// virtualises rendering, so high-degree nodes (Anakin: 671 edges) scroll smoothly.
    /// </summary>
    [HttpGet("edges/{pageId:int}")]
    public Task<EntityEdgesResult> GetEdges(int pageId, [FromQuery] int limit = 500, CancellationToken ct = default) => kg.GetEdgesForEntityAsync(pageId, limit, ct);

    /// <summary>
    /// All Active node-property enrichments for an entity with full claim + evidence detail.
    /// Powers the dedicated "Holocron-added attributes" panel beside the Attributes table
    /// on the Knowledge Graph node-detail panel. Phase 2 contributions are surfaced separately
    /// from the Phase 1 infobox so users can see exactly what the agent added and why.
    /// </summary>
    [HttpGet("node-enrichments/{pageId:int}")]
    public Task<EntityNodeEnrichmentsResult> GetNodeEnrichments(int pageId, CancellationToken ct = default) => kg.GetNodeEnrichmentsAsync(pageId, ct);

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
        // `realm` is the canonical query-string key (matches GlobalFilterService.GetRealmQueryParam).
        // `universe` is kept as a legacy alias so any external caller still works; coalesced below.
        [FromQuery] string? realm = null,
        [FromQuery] string? universe = null,
        [FromQuery] int? yearFrom = null,
        [FromQuery] int? yearTo = null,
        [FromQuery] int maxNodes = 200,
        CancellationToken ct = default
    ) => kg.QueryGraphAsync(pageId, labels, maxDepth, continuity, onlyRoot, realm ?? universe, yearFrom, yearTo, maxNodes, ct);
}
