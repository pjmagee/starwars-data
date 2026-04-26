using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents;

/// <summary>
/// Phase 2 background agent. Reads <c>kg.nodes</c>, gathers context from related
/// nodes / edges / article chunks, and writes evidence-backed enrichments to
/// <c>kg.enrichments</c> / <c>kg.edge_enrichments</c>. Every state transition is
/// recorded in <c>kg.events</c> as an immutable audit log, which the Stage D
/// changelog UI will paginate over.
///
/// **Contract** (see <c>eng/design/018-kg-enrichments-architecture.md</c>):
/// <list type="bullet">
///   <item>Never writes to <c>kg.nodes</c> or <c>kg.edges</c> — those belong to Phase 1.</item>
///   <item>Every enrichment carries at least one evidence excerpt with a real source citation.</item>
///   <item>Active enrichments stamp <c>contentHashAtCreation</c> from the source node; the
///         post-Phase-1 staleness sweep flips mismatched ones to <see cref="EnrichmentStatus.Stale"/>
///         rather than deleting them.</item>
///   <item><see cref="EnrichmentOperation.Refine"/> proposals (correcting an existing infobox
///         value) are surfaced as suggestions only — never auto-applied.</item>
/// </list>
///
/// **Stage B** (this file): skeleton with stub method bodies. The collections are real;
/// the views are real; the LLM enhancement logic ships in Stage C.
/// </summary>
public sealed class HolocronAgent
{
    /// <summary>Bumped on every meaningful change to the enhancement prompt or schema. Stamped onto every enrichment + event.</summary>
    public const string AgentVersion = "holocron-v0.1.0-skeleton";

    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<NodeEnrichment> _enrichments;
    readonly IMongoCollection<EdgeEnrichment> _edgeEnrichments;
    readonly IMongoCollection<HolocronEvent> _events;
    readonly IChatClient _chatClient;
    readonly ILogger<HolocronAgent> _logger;
    readonly SettingsOptions _settings;

    public HolocronAgent(IMongoClient mongoClient, IOptions<SettingsOptions> settings, IChatClient chatClient, ILogger<HolocronAgent> logger)
    {
        _settings = settings.Value;
        var db = mongoClient.GetDatabase(_settings.DatabaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _enrichments = db.GetCollection<NodeEnrichment>(Collections.KgEnrichments);
        _edgeEnrichments = db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
        _events = db.GetCollection<HolocronEvent>(Collections.KgEvents);
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Daily orchestrator: emits <see cref="HolocronEventType.HolocronPassStarted"/>,
    /// runs the staleness sweep, picks a batch of nodes to enhance, and emits
    /// <see cref="HolocronEventType.HolocronPassCompleted"/> with summary counts.
    ///
    /// Stage B: emits the bookkeeping events but does no enhancement.
    /// </summary>
    public async Task RunDailyPassAsync(CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        await EmitEventAsync(
            new HolocronEvent
            {
                EventType = HolocronEventType.HolocronPassStarted,
                Summary = "Holocron daily pass started.",
                TriggeredBy = "scheduled",
                AgentVersion = AgentVersion,
            },
            ct
        );

        var stalenessSummary = await RunStalenessSweepAsync(ct);

        // Stage C will: select target nodes, call EnhanceNodeAsync per node, batch + emit.
        var enhanced = 0;

        await EmitEventAsync(
            new HolocronEvent
            {
                EventType = HolocronEventType.HolocronPassCompleted,
                Summary = $"Holocron daily pass complete. Stale flagged: {stalenessSummary.MarkedStale}, enhanced: {enhanced}.",
                TriggeredBy = "scheduled",
                AgentVersion = AgentVersion,
            },
            ct
        );

        _logger.LogInformation("HolocronAgent: daily pass completed in {Duration}. Stale: {Stale}, enhanced: {Enhanced}.", DateTime.UtcNow - started, stalenessSummary.MarkedStale, enhanced);
    }

    /// <summary>
    /// Walk every <see cref="EnrichmentStatus.Active"/> enrichment and compare its
    /// <see cref="NodeEnrichment.ContentHashAtCreation"/> against the current source
    /// node's <see cref="GraphNode.ContentHash"/>. Mismatches flip to
    /// <see cref="EnrichmentStatus.Stale"/> (orphans — node deleted upstream — get
    /// the same status with reason <c>"page_deleted"</c>).
    ///
    /// Runs after every Phase 1 rebuild, before the daily enhancement pass.
    /// </summary>
    public async Task<StalenessSweepSummary> RunStalenessSweepAsync(CancellationToken ct = default)
    {
        // Stage B placeholder. Stage C wires the real comparison + bulk update.
        // Sketch:
        //   var active = await _enrichments.Find(e => e.Status == Active).ToListAsync(ct);
        //   var nodeHashes = await _nodes.Find(...).Project(n => new {n.PageId, n.ContentHash}).ToDictionaryAsync(...);
        //   foreach mismatched: bulkWrite UpdateOne {status = Stale} + EmitEventAsync(EnrichmentMarkedStale)

        _logger.LogInformation("HolocronAgent: staleness sweep stub — no work yet (Stage B).");
        return new StalenessSweepSummary(MarkedStale: 0, MarkedOrphaned: 0, Total: 0);
    }

    /// <summary>
    /// Enhance one node: gather context, ask the LLM for evidence-backed enrichment
    /// proposals, validate evidence citations, supersede prior active enrichments
    /// for matching <c>(pageId, fieldPath)</c> tuples, and append the new ones plus
    /// <see cref="HolocronEventType.EnrichmentCreated"/> events.
    ///
    /// Stage B: validates the node exists, logs, returns. The LLM call lands in Stage C.
    /// </summary>
    public async Task<NodeEnhancementSummary> EnhanceNodeAsync(int pageId, string triggeredBy = "manual", CancellationToken ct = default)
    {
        var node = await _nodes.Find(Builders<GraphNode>.Filter.Eq(n => n.PageId, pageId)).FirstOrDefaultAsync(ct);
        if (node is null)
        {
            _logger.LogWarning("HolocronAgent: EnhanceNodeAsync called for missing PageId={PageId}", pageId);
            return new NodeEnhancementSummary(pageId, EnrichmentsCreated: 0, EvidenceFailures: 0);
        }

        // Stage C wiring will go here:
        //   1. Gather 1-2 hop neighbours via kg.edges.bidir
        //   2. Pull ArticleChunks for the source page + cited cross-refs
        //   3. Single LLM call with structured-output schema for enrichment proposals
        //   4. Validate evidence citations against real PageIds / chunkIds
        //   5. Supersede prior active enrichments for matching (pageId, fieldPath)
        //   6. Insert new NodeEnrichment docs with status=Active, contentHashAtCreation=node.ContentHash
        //   7. Emit one EnrichmentCreated HolocronEvent per insert

        _logger.LogInformation(
            "HolocronAgent: EnhanceNodeAsync stub for PageId={PageId} ({Name}, type={Type}, contentHash={Hash}) — no enrichments produced (Stage B).",
            pageId,
            node.Name,
            node.Type,
            node.ContentHash ?? "<null>"
        );

        return new NodeEnhancementSummary(pageId, EnrichmentsCreated: 0, EvidenceFailures: 0);
    }

    /// <summary>Insert a single event into the audit log. Never updates existing events.</summary>
    Task EmitEventAsync(HolocronEvent evt, CancellationToken ct) => _events.InsertOneAsync(evt, cancellationToken: ct);
}

/// <summary>Summary of one staleness sweep — counts of enrichments transitioned.</summary>
public sealed record StalenessSweepSummary(int MarkedStale, int MarkedOrphaned, int Total);

/// <summary>Summary of one <see cref="HolocronAgent.EnhanceNodeAsync"/> call.</summary>
public sealed record NodeEnhancementSummary(int PageId, int EnrichmentsCreated, int EvidenceFailures);
