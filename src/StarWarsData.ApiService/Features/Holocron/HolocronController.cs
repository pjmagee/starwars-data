using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.AI.Agents;
using StarWarsData.Services.AI.Agents.Holocron;

namespace StarWarsData.ApiService.Features.Holocron;

/// <summary>
/// Public-read / admin-write surface for the Holocron agent.
///
/// <list type="bullet">
///   <item><c>POST /api/holocron/jobs/enhance/{pageId}</c> — kicks off an async enhancement
///         workflow (Design-020). Returns 202 with the new job id. The workflow runs
///         in-process via <see cref="HolocronEnhancementService"/>; client polls the
///         status endpoint for live progress.</item>
///   <item><c>GET /api/holocron/jobs/{pageId}/status</c> — live progress for the
///         currently-running (or just-completed) enhancement of this node, including
///         the per-stage activity log streamed from the workflow.</item>
///   <item><c>GET /api/holocron/jobs/active</c> — list of nodes currently being enhanced.</item>
///   <item><c>GET /api/holocron/jobs</c> — paginated history from <c>kg.enrichment_jobs</c>.</item>
///   <item><c>GET /api/holocron/jobs/last-completed/{pageId}</c> — most recent completed
///         job for a node (powers the "last processed: X minutes ago" caption).</item>
///   <item><c>GET /api/holocron/events</c> — paginated audit log (Holocron Log page).</item>
///   <item><c>GET /api/holocron/enrichments/{id}</c> — full enrichment detail.</item>
/// </list>
///
/// All workflow runs stamp <c>triggeredBy: "manual"</c> on the job-doc and downstream
/// events, distinguishing user-initiated runs from scheduled passes.
/// </summary>
[ApiController]
[Route("api/holocron")]
[Produces("application/json")]
public class HolocronController : ControllerBase
{
    readonly HolocronAgent _agent;
    readonly HolocronJobService _jobService;
    readonly HolocronEnhancementTracker _tracker;
    readonly IServiceScopeFactory _scopeFactory;
    readonly ILogger<HolocronController> _logger;
    readonly IOptions<SettingsOptions> _settings;
    readonly IMongoDatabase _db;

    public HolocronController(
        HolocronAgent agent,
        HolocronJobService jobService,
        HolocronEnhancementTracker tracker,
        IServiceScopeFactory scopeFactory,
        ILogger<HolocronController> logger,
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings
    )
    {
        _agent = agent;
        _jobService = jobService;
        _tracker = tracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings;
        _db = mongoClient.GetDatabase(settings.Value.DatabaseName);
    }

    IMongoCollection<HolocronEvent> Events => _db.GetCollection<HolocronEvent>(Collections.KgEvents);
    IMongoCollection<NodeEnrichment> NodeEnrichments => _db.GetCollection<NodeEnrichment>(Collections.KgEnrichments);
    IMongoCollection<EdgeEnrichment> EdgeEnrichments => _db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
    IMongoCollection<GraphNode> Nodes => _db.GetCollection<GraphNode>(Collections.KgNodes);

    // ── Async enhancement kickoff ─────────────────────────────────────────

    /// <summary>
    /// Kicks off a Holocron enhancement workflow for one node and returns immediately.
    /// The workflow runs in-process via <see cref="HolocronEnhancementService"/> on a
    /// fresh DI scope; clients poll <c>/jobs/{pageId}/status</c> for progress.
    ///
    /// Honours <see cref="SettingsOptions.HolocronEnabled"/> as a kill switch (503 when
    /// off). Returns 409 if a non-terminal run already exists for this node — the
    /// per-page invariant from Design-020.
    /// </summary>
    [HttpPost("jobs/enhance/{pageId:int}")]
    public async Task<IActionResult> EnhanceNode(int pageId, CancellationToken ct)
    {
        if (!_settings.Value.HolocronEnabled)
            return StatusCode(503, new { error = "Holocron is disabled (Settings.HolocronEnabled = false)." });

        if (_tracker.IsRunning(pageId))
            return Conflict(new { error = "Enhancement already in progress for this node." });

        var node = await Nodes.Find(n => n.PageId == pageId).Project(n => new { n.PageId, n.Name }).FirstOrDefaultAsync(ct);
        if (node is null)
            return NotFound(new { error = $"Node {pageId} not found." });

        var job = await _jobService.EnqueueOrGetActiveAsync(pageId, node.Name, "manual", HolocronAgent.AgentVersion, _settings.Value.HolocronModel, ct);

        if (!_tracker.TryStart(pageId, node.Name, job.Id))
            return Conflict(new { error = "Enhancement already in progress for this node." });

        // Fire-and-forget on a fresh DI scope. Same pattern as
        // CharacterTimelinesController.Generate — `Task.Run` with scope makes the
        // controller return 202 immediately while the workflow runs in the background.
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<HolocronEnhancementService>();
            try
            {
                await service.RunAsync(job.Id, pageId, "manual", _tracker, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Holocron pipeline failed — JobId={JobId} PageId={PageId}", job.Id, pageId);
                _tracker.Fail(pageId, ex.Message);
                await _jobService.FailAsync(job.Id, ex.Message, CancellationToken.None);
            }
        });

        return Accepted(
            new
            {
                jobId = job.Id,
                pageId,
                status = job.Status.ToString(),
            }
        );
    }

    // ── Status / list endpoints ───────────────────────────────────────────

    /// <summary>
    /// Live status for the most recent run of <paramref name="pageId"/>. Returns the
    /// in-memory tracker entry if one exists (fastest path), else falls back to
    /// the most recent <c>kg.enrichment_jobs</c> row.
    /// </summary>
    [HttpGet("jobs/{pageId:int}/status")]
    public async Task<ActionResult<HolocronStatusDto>> GetStatus(int pageId, CancellationToken ct)
    {
        var live = _tracker.GetStatus(pageId);
        if (live is not null)
        {
            return Ok(
                new HolocronStatusDto(
                    JobId: live.JobId,
                    PageId: pageId,
                    NodeName: live.NodeName,
                    Stage: live.Stage,
                    Message: live.Message,
                    Error: live.Error,
                    StartedAt: live.StartedAt,
                    CompletedAt: live.Stage is HolocronJobStatus.Completed or HolocronJobStatus.Failed ? DateTime.UtcNow : null,
                    CurrentStep: live.CurrentStep,
                    TotalSteps: live.TotalSteps,
                    CurrentItem: live.CurrentItem,
                    ProposalsExtracted: live.ProposalsExtracted,
                    EnrichmentsApplied: live.EnrichmentsApplied,
                    ActivityLog: live.ActivityLog
                )
            );
        }

        // Fall back to the most recent persisted job for this page.
        var jobs = await _jobService.ListAsync(new HolocronJobQuery(PageId: pageId, PageSize: 1), ct);
        var latest = jobs.Items.FirstOrDefault();
        if (latest is null)
            return NotFound();

        return Ok(
            new HolocronStatusDto(
                JobId: latest.Id,
                PageId: latest.PageId,
                NodeName: latest.NodeName,
                Stage: latest.Status,
                Message: latest.Status == HolocronJobStatus.Failed && !string.IsNullOrEmpty(latest.Error) ? latest.Error : latest.Status.ToString(),
                Error: latest.Error,
                StartedAt: latest.StartedAt ?? latest.CreatedAt,
                CompletedAt: latest.CompletedAt,
                CurrentStep: 0,
                TotalSteps: 0,
                CurrentItem: null,
                ProposalsExtracted: latest.ProposalsExtracted,
                EnrichmentsApplied: latest.EnrichmentsApplied,
                ActivityLog: []
            )
        );
    }

    /// <summary>List of currently-active enhancement runs (anything not in a terminal state).</summary>
    [HttpGet("jobs/active")]
    public ActionResult<List<HolocronActiveDto>> GetActive() =>
        Ok(
            _tracker
                .GetActiveStatuses()
                .Select(a => new HolocronActiveDto(
                    JobId: a.Status.JobId,
                    PageId: a.PageId,
                    NodeName: a.Status.NodeName,
                    Stage: a.Status.Stage,
                    Message: a.Status.Message,
                    StartedAt: a.Status.StartedAt,
                    CurrentStep: a.Status.CurrentStep,
                    TotalSteps: a.Status.TotalSteps,
                    ProposalsExtracted: a.Status.ProposalsExtracted,
                    EnrichmentsApplied: a.Status.EnrichmentsApplied
                ))
                .ToList()
        );

    /// <summary>Paginated job history from <c>kg.enrichment_jobs</c>.</summary>
    [HttpGet("jobs")]
    public async Task<ActionResult<HolocronJobsPage>> ListJobs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] HolocronJobStatus? status = null,
        [FromQuery] int? pageId = null,
        CancellationToken ct = default
    )
    {
        var query = new HolocronJobQuery(Statuses: status.HasValue ? [status.Value] : null, PageId: pageId, Page: page, PageSize: pageSize);
        var result = await _jobService.ListAsync(query, ct);
        return Ok(result);
    }

    /// <summary>Most recent completed job for a node — drives the "last processed N ago" caption.</summary>
    [HttpGet("jobs/last-completed/{pageId:int}")]
    public async Task<ActionResult<HolocronJob>> GetLastCompleted(int pageId, CancellationToken ct)
    {
        var job = await _jobService.GetLastCompletedForNodeAsync(pageId, ct);
        if (job is null)
            return NotFound();
        return Ok(job);
    }

    /// <summary>
    /// Unified list of every Holocron enrichment touching <paramref name="pageId"/> —
    /// node-property enrichments where the node IS the subject, plus edge enrichments
    /// where the node is either endpoint. Used by <c>/holocron/jobs/{pageId}</c>'s
    /// "Applied changes" section so users can see WHAT the agent did, not just that
    /// runs happened. Sorted newest-first; status defaults to Active but accepts
    /// <c>?includeStale=true</c> to surface superseded entries too.
    /// </summary>
    [HttpGet("jobs/{pageId:int}/enrichments")]
    public async Task<ActionResult<List<HolocronNodeEnrichmentDto>>> GetNodeEnrichments(int pageId, [FromQuery] bool includeStale = false, CancellationToken ct = default)
    {
        var statusFilter = includeStale ? Builders<NodeEnrichment>.Filter.Empty : Builders<NodeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active);
        var edgeStatusFilter = includeStale ? Builders<EdgeEnrichment>.Filter.Empty : Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active);

        var nodeRowsTask = NodeEnrichments
            .Find(Builders<NodeEnrichment>.Filter.Eq(e => e.PageId, pageId) & statusFilter)
            .SortByDescending(e => e.AppliedAt)
            .ThenByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
        var edgeRowsTask = EdgeEnrichments
            .Find((Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, pageId) | Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, pageId)) & edgeStatusFilter)
            .SortByDescending(e => e.AppliedAt)
            .ThenByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

        await Task.WhenAll(nodeRowsTask, edgeRowsTask);
        var nodeRows = nodeRowsTask.Result;
        var edgeRows = edgeRowsTask.Result;

        // Resolve all referenced node names (edges' from/to) in one shot so each row
        // can render `Anakin Skywalker -[affiliated_with]-> Jedi Order` directly.
        var endpointIds = edgeRows.SelectMany(e => new[] { e.FromId, e.ToId }).Distinct().ToList();
        var nameByPageId =
            endpointIds.Count == 0
                ? new Dictionary<int, string>()
                : (await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, endpointIds)).Project(n => new { n.PageId, n.Name }).ToListAsync(ct)).ToDictionary(x => x.PageId, x => x.Name);

        var rows = new List<HolocronNodeEnrichmentDto>();

        foreach (var e in nodeRows)
        {
            rows.Add(
                new HolocronNodeEnrichmentDto(
                    Id: e.Id,
                    Kind: "node",
                    Operation: e.Operation.ToString(),
                    Status: e.Status.ToString(),
                    FromId: null,
                    FromName: null,
                    ToId: null,
                    ToName: null,
                    Label: null,
                    FieldPath: e.FieldPath,
                    ValueJson: e.Value.ToJson(),
                    Claim: e.Claim,
                    EvidenceCount: e.Evidence.Count,
                    AppliedAt: e.AppliedAt ?? e.CreatedAt,
                    JobId: string.IsNullOrEmpty(e.JobId) ? null : e.JobId,
                    AgentVersion: e.AgentVersion
                )
            );
        }

        foreach (var e in edgeRows)
        {
            rows.Add(
                new HolocronNodeEnrichmentDto(
                    Id: e.Id,
                    Kind: "edge",
                    Operation: e.Operation.ToString(),
                    Status: e.Status.ToString(),
                    FromId: e.FromId,
                    FromName: nameByPageId.GetValueOrDefault(e.FromId, $"#{e.FromId}"),
                    ToId: e.ToId,
                    ToName: nameByPageId.GetValueOrDefault(e.ToId, $"#{e.ToId}"),
                    Label: e.Label,
                    FieldPath: null,
                    ValueJson: e.Value.ToJson(),
                    Claim: e.Claim,
                    EvidenceCount: e.Evidence.Count,
                    AppliedAt: e.AppliedAt ?? e.CreatedAt,
                    JobId: string.IsNullOrEmpty(e.JobId) ? null : e.JobId,
                    AgentVersion: e.AgentVersion
                )
            );
        }

        // Mixed sort by timestamp so node + edge enrichments interleave correctly.
        return Ok(rows.OrderByDescending(r => r.AppliedAt).ToList());
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
        [FromQuery] string? nodeType = null,
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
        {
            // Match events where the entity is the subject (pageId) OR where it's either endpoint
            // of an edge enrichment (fromId / toId). Edge enrichments don't populate pageId, so a
            // strict pageId match would hide edge events for the queried entity — bad UX.
            filters.Add(
                Builders<HolocronEvent>.Filter.Or(
                    Builders<HolocronEvent>.Filter.Eq(e => e.PageId, pid),
                    Builders<HolocronEvent>.Filter.Eq(e => e.FromId, pid),
                    Builders<HolocronEvent>.Filter.Eq(e => e.ToId, pid)
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(nodeType))
        {
            // Resolve the type → pageIds via kg.nodes, then filter events to those whose
            // subject (pageId/fromId/toId) lands in the set. The events collection is bounded
            // (audit trail, low cardinality), so an $in on the pageIds — even thousands of them —
            // performs fine. Empty result if the type matches no nodes.
            var pageIdsOfType = await Nodes.Find(Builders<GraphNode>.Filter.Eq(n => n.Type, nodeType)).Project(n => n.PageId).ToListAsync(ct);
            if (pageIdsOfType.Count == 0)
                return Ok(new HolocronEventsPage([], 0, page, pageSize));
            filters.Add(
                Builders<HolocronEvent>.Filter.Or(
                    Builders<HolocronEvent>.Filter.In(e => e.PageId, pageIdsOfType),
                    Builders<HolocronEvent>.Filter.In(e => e.FromId, pageIdsOfType),
                    Builders<HolocronEvent>.Filter.In(e => e.ToId, pageIdsOfType)
                )
            );
        }
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

/// <summary>Live or persisted status for one Holocron run, returned by <c>GET /jobs/{pageId}/status</c>.</summary>
public sealed record HolocronStatusDto(
    string? JobId,
    int PageId,
    string? NodeName,
    HolocronJobStatus Stage,
    string Message,
    string? Error,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int CurrentStep,
    int TotalSteps,
    string? CurrentItem,
    int ProposalsExtracted,
    int EnrichmentsApplied,
    List<HolocronActivityLogEntry> ActivityLog
);

/// <summary>One row in the active-runs list returned by <c>GET /jobs/active</c>.</summary>
public sealed record HolocronActiveDto(
    string? JobId,
    int PageId,
    string? NodeName,
    HolocronJobStatus Stage,
    string Message,
    DateTime StartedAt,
    int CurrentStep,
    int TotalSteps,
    int ProposalsExtracted,
    int EnrichmentsApplied
);

/// <summary>
/// One row in the unified node + edge enrichment list returned by
/// <c>GET /jobs/{pageId}/enrichments</c>. Powers the "Applied changes" section
/// on the per-node Holocron jobs page.
///
/// <see cref="Kind"/> is <c>"node"</c> for property enrichments (FieldPath populated)
/// or <c>"edge"</c> for relationship enrichments (FromId/ToId/Label populated). The
/// frontend switches rendering on Kind so node-prop and edge rows can sit in the
/// same time-sorted list without needing two separate fetches.
/// </summary>
public sealed record HolocronNodeEnrichmentDto(
    string Id,
    string Kind,
    string Operation,
    string Status,
    int? FromId,
    string? FromName,
    int? ToId,
    string? ToName,
    string? Label,
    string? FieldPath,
    string ValueJson,
    string Claim,
    int EvidenceCount,
    DateTime AppliedAt,
    string? JobId,
    string AgentVersion
);
