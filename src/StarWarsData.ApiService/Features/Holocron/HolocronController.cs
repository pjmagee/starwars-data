using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.AI.Agents;

namespace StarWarsData.ApiService.Features.Holocron;

/// <summary>
/// Public-read / admin-write surface for the Holocron agent.
///
/// <list type="bullet">
///   <item><c>POST /api/holocron/enhance/{pageId}</c> — admin only. Synchronous single-node
///         enhancement. Honours <see cref="SettingsOptions.HolocronEnabled"/> as a kill switch.</item>
///   <item><c>GET /api/holocron/events</c> — public. Paginated audit log for the Holocron Log page.</item>
///   <item><c>GET /api/holocron/enrichments/{id}</c> — public. Full enrichment detail (claim, evidence,
///         reasoning) for the row-expand view.</item>
/// </list>
///
/// The agent always stamps <c>triggeredBy: "manual"</c> on events from the enhance endpoint
/// so the changelog distinguishes button-clicks from scheduled-pass output.
/// </summary>
[ApiController]
[Route("api/holocron")]
[Produces("application/json")]
public class HolocronController(HolocronAgent agent, IMongoClient mongoClient, IOptions<SettingsOptions> settings) : ControllerBase
{
    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);
    IMongoCollection<HolocronEvent> Events => _db.GetCollection<HolocronEvent>(Collections.KgEvents);
    IMongoCollection<NodeEnrichment> NodeEnrichments => _db.GetCollection<NodeEnrichment>(Collections.KgEnrichments);
    IMongoCollection<EdgeEnrichment> EdgeEnrichments => _db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
    IMongoCollection<GraphNode> Nodes => _db.GetCollection<GraphNode>(Collections.KgNodes);

    // ── Admin-only: trigger enhancement for one node ──────────────────────

    /// <summary>
    /// Synchronous single-node enhancement. Bypasses the daily-pass selection logic —
    /// the caller has explicitly chosen this node. Returns a summary of how many
    /// enrichments were created and how many proposals failed evidence validation.
    ///
    /// Open to any authenticated caller — the operation is bounded by Holocron's own
    /// pre-flight rules (canonical labels only, no parallel edges, evidence required)
    /// and the agent's pass-budget settings, so a public refresh-button doesn't open
    /// any new abuse vectors. The kill switch <see cref="SettingsOptions.HolocronEnabled"/>
    /// returns 503 when off.
    /// </summary>
    [HttpPost("enhance/{pageId:int}")]
    public async Task<ActionResult<NodeEnhancementSummary>> EnhanceNode(int pageId, CancellationToken ct)
    {
        if (!settings.Value.HolocronEnabled)
            return StatusCode(503, new { error = "Holocron is disabled (Settings.HolocronEnabled = false)." });

        var summary = await agent.EnhanceNodeAsync(pageId, "manual", ct);
        return Ok(summary);
    }

    // ── Public: paginated audit log ───────────────────────────────────────

    /// <summary>
    /// Paginated list of Holocron events, newest first. Filterable by event type and
    /// the affected node's PageId. Joins <c>kg.nodes.name</c> for each event so the
    /// frontend can render entity names without a second round-trip per row.
    /// </summary>
    [HttpGet("events")]
    public async Task<ActionResult<HolocronEventsPage>> ListEvents(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? eventType = null,
        [FromQuery] int? pageId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default
    )
    {
        if (page < 1)
            page = 1;
        if (pageSize is < 1 or > 100)
            pageSize = 20;

        var filters = new List<FilterDefinition<HolocronEvent>>();
        if (!string.IsNullOrWhiteSpace(eventType) && Enum.TryParse<HolocronEventType>(eventType, ignoreCase: true, out var parsed))
            filters.Add(Builders<HolocronEvent>.Filter.Eq(e => e.EventType, parsed));
        if (pageId is { } pid && pid > 0)
            filters.Add(Builders<HolocronEvent>.Filter.Eq(e => e.PageId, pid));
        if (from.HasValue)
            filters.Add(Builders<HolocronEvent>.Filter.Gte(e => e.OccurredAt, from.Value));
        if (to.HasValue)
            filters.Add(Builders<HolocronEvent>.Filter.Lte(e => e.OccurredAt, to.Value));

        var filter = filters.Count > 0 ? Builders<HolocronEvent>.Filter.And(filters) : FilterDefinition<HolocronEvent>.Empty;

        var totalTask = Events.CountDocumentsAsync(filter, cancellationToken: ct);
        var rowsTask = Events.Find(filter).SortByDescending(e => e.OccurredAt).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);

        await Task.WhenAll(totalTask, rowsTask);
        var total = (int)totalTask.Result;
        var rows = rowsTask.Result;

        // Join the source-node names in one batch so the page renders entity labels
        // without a second roundtrip per row.
        var pageIds = rows.SelectMany(e => new[] { e.PageId, e.FromId, e.ToId }).Where(id => id > 0).Distinct().ToList();

        var nameByPageId = pageIds.Count == 0 ? [] : await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, pageIds)).Project(n => new { n.PageId, n.Name }).ToListAsync(ct);
        var nameMap = nameByPageId.ToDictionary(x => x.PageId, x => x.Name);

        var items = rows.Select(e => new HolocronEventDto(
                Id: e.Id,
                EventType: e.EventType.ToString(),
                Summary: e.Summary,
                OccurredAt: e.OccurredAt,
                PageId: e.PageId > 0 ? e.PageId : null,
                NodeName: e.PageId > 0 && nameMap.TryGetValue(e.PageId, out var n) ? n : null,
                FieldPath: e.FieldPath,
                FromId: e.FromId > 0 ? e.FromId : null,
                ToId: e.ToId > 0 ? e.ToId : null,
                FromName: e.FromId > 0 && nameMap.TryGetValue(e.FromId, out var fn) ? fn : null,
                ToName: e.ToId > 0 && nameMap.TryGetValue(e.ToId, out var tn) ? tn : null,
                Label: e.Label,
                EnrichmentId: e.EnrichmentId,
                TriggeredBy: e.TriggeredBy,
                AgentVersion: e.AgentVersion
            ))
            .ToList();

        return Ok(new HolocronEventsPage(items, total, page, pageSize));
    }

    // ── Public: full enrichment detail ────────────────────────────────────

    /// <summary>
    /// Full enrichment detail for the row-expand view: claim, evidence excerpts (with
    /// source-page names joined), agent reasoning, status, content-hash. Looks up
    /// <c>kg.enrichments</c> first, then falls back to <c>kg.edge_enrichments</c>.
    /// Returns 404 if neither holds the id.
    /// </summary>
    [HttpGet("enrichments/{id}")]
    public async Task<ActionResult<HolocronEnrichmentDetailDto>> GetEnrichment(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _))
            return BadRequest(new { error = "Invalid enrichment id." });

        var node = await NodeEnrichments.Find(e => e.Id == id).FirstOrDefaultAsync(ct);
        if (node is not null)
            return Ok(await BuildNodeDetailAsync(node, ct));

        var edge = await EdgeEnrichments.Find(e => e.Id == id).FirstOrDefaultAsync(ct);
        if (edge is not null)
            return Ok(await BuildEdgeDetailAsync(edge, ct));

        return NotFound(new { error = "Enrichment not found." });
    }

    async Task<HolocronEnrichmentDetailDto> BuildNodeDetailAsync(NodeEnrichment e, CancellationToken ct)
    {
        var citedPageIds = e.Evidence.Where(ev => ev.SourcePageId > 0).Select(ev => ev.SourcePageId).Append(e.PageId).Distinct().ToList();
        var nameMap = await ResolvePageNamesAsync(citedPageIds, ct);

        return new HolocronEnrichmentDetailDto(
            Id: e.Id,
            Kind: "node",
            PageId: e.PageId,
            FieldPath: e.FieldPath,
            FromId: null,
            ToId: null,
            Label: null,
            Operation: e.Operation.ToString(),
            ValueJson: e.Value.ToJson(),
            Claim: e.Claim,
            Evidence: MapEvidence(e.Evidence, nameMap),
            LlmReasoning: e.LlmReasoning,
            Status: e.Status.ToString(),
            CreatedAt: e.CreatedAt,
            AgentVersion: e.AgentVersion,
            ModelId: e.ModelId,
            NodeName: nameMap.GetValueOrDefault(e.PageId),
            FromName: null,
            ToName: null
        );
    }

    async Task<HolocronEnrichmentDetailDto> BuildEdgeDetailAsync(EdgeEnrichment e, CancellationToken ct)
    {
        var citedPageIds = e.Evidence.Where(ev => ev.SourcePageId > 0).Select(ev => ev.SourcePageId).Concat([e.FromId, e.ToId]).Distinct().ToList();
        var nameMap = await ResolvePageNamesAsync(citedPageIds, ct);

        return new HolocronEnrichmentDetailDto(
            Id: e.Id,
            Kind: "edge",
            PageId: null,
            FieldPath: null,
            FromId: e.FromId,
            ToId: e.ToId,
            Label: e.Label,
            Operation: e.Operation.ToString(),
            ValueJson: e.Value.ToJson(),
            Claim: e.Claim,
            Evidence: MapEvidence(e.Evidence, nameMap),
            LlmReasoning: e.LlmReasoning,
            Status: e.Status.ToString(),
            CreatedAt: e.CreatedAt,
            AgentVersion: e.AgentVersion,
            ModelId: e.ModelId,
            NodeName: null,
            FromName: nameMap.GetValueOrDefault(e.FromId),
            ToName: nameMap.GetValueOrDefault(e.ToId)
        );
    }

    async Task<Dictionary<int, string>> ResolvePageNamesAsync(List<int> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
            return [];
        var rows = await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, pageIds)).Project(n => new { n.PageId, n.Name }).ToListAsync(ct);
        return rows.ToDictionary(x => x.PageId, x => x.Name);
    }

    static List<HolocronEvidenceDto> MapEvidence(List<EnrichmentEvidence> evidence, Dictionary<int, string> nameMap) =>
        evidence
            .Select(ev => new HolocronEvidenceDto(
                SourcePageId: ev.SourcePageId > 0 ? ev.SourcePageId : null,
                SourcePageName: ev.SourcePageId > 0 && nameMap.TryGetValue(ev.SourcePageId, out var n) ? n : null,
                ChunkId: ev.ChunkId,
                Excerpt: ev.Excerpt,
                RelevanceScore: ev.RelevanceScore
            ))
            .ToList();
}
