using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
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

    /// <summary>
    /// Single-node TemporalNodeDto by PageId — backs the standalone
    /// <c>/knowledge-graph/nodes/{id}</c> detail page. Same shape as a row inside
    /// <see cref="BrowseTemporalNodes"/> so the same <see cref="NodeDetailPanel"/> UI
    /// works against both surfaces. Returns 404 when the node does not exist.
    /// </summary>
    [HttpGet("temporal-nodes/{pageId:int}")]
    public async Task<ActionResult<TemporalNodeDto>> GetTemporalNode(int pageId, CancellationToken ct = default)
    {
        var node = await kg.GetTemporalNodeAsync(pageId, ct);
        return node is null ? NotFound() : Ok(node);
    }

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

    /// <summary>
    /// Marriage-aware, generation-aligned family-tree projection for a Character node.
    /// Backs the <c>render_family_tree</c> AI tool and the <c>FamilyTreeView.razor</c>
    /// component. Wire shape and error semantics:
    /// <c>specs/042-family-tree-component/contracts/family-tree-endpoint.md</c>.
    /// </summary>
    [HttpGet("family-tree/{pageId:int}")]
    public async Task<IActionResult> FamilyTree(
        int pageId,
        [FromQuery] int maxDepth = 3,
        [FromQuery] string? continuity = null,
        // `realm` mirrors the QueryGraph endpoint's GlobalFilterService passthrough.
        // `universe` is the legacy alias coalesced into `realm`.
        [FromQuery] string? realm = null,
        [FromQuery] string? universe = null,
        CancellationToken ct = default
    )
    {
        // Validate continuity early so an unknown value short-circuits before we
        // hit Mongo. ParseContinuityFilter inside the service silently drops
        // unknown values; the contract for this endpoint is stricter.
        if (!string.IsNullOrWhiteSpace(continuity) && !IsValidContinuity(continuity))
            return BadRequest(
                new
                {
                    error = "InvalidQueryParameter",
                    name = "continuity",
                    value = continuity,
                }
            );

        var resolvedRealm = realm ?? universe;
        if (!string.IsNullOrWhiteSpace(resolvedRealm) && !Enum.TryParse<Realm>(resolvedRealm, true, out _))
            return BadRequest(
                new
                {
                    error = "InvalidQueryParameter",
                    name = "realm",
                    value = resolvedRealm,
                }
            );

        var nodeInfo = await kg.GetNodeNameAndTypeAsync(pageId, ct);
        if (nodeInfo is null)
            return NotFound(new { error = "RootNotFound", pageId });

        if (!string.Equals(nodeInfo.Value.Type, KgNodeTypes.Character, StringComparison.Ordinal))
            return BadRequest(new { error = "RootMustBeCharacter", actualType = nodeInfo.Value.Type });

        var response = await kg.BuildFamilyTreeAsync(pageId, maxDepth, continuity, resolvedRealm, maxNodes: 200, ct);
        return Ok(response);
    }

    private static bool IsValidContinuity(string value) => string.Equals(value, "Canon", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Legends", StringComparison.OrdinalIgnoreCase);
}
