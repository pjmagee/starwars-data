using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 1 of the Holocron workflow (Design-020). Pure C# — no LLM. Builds the
/// "everything-the-extractor-needs" snapshot for the target node and populates
/// the workflow state for downstream stages.
///
/// Concretely:
///
/// <list type="number">
///   <item>Loads the target node (snapshotted to <see cref="HolocronNodeSnapshot"/>).</item>
///   <item>Loads the top-K outgoing + incoming edges (sorted by weight) plus
///         neighbour summaries for the prompt's "Edges available for ANNOTATE"
///         and "Edges with null temporal bounds" lists.</item>
///   <item>Loads the top-K canonical edge labels for the node's type — pre-flight
///         rejects any <c>addEdges</c> proposal whose label isn't in this set.</item>
///   <item>Queries every chunk that wikilinks to the target's <c>WikiUrl</c> via
///         the multikey <c>links</c> index (populated by migration 0012).
///         No per-page cap. No total limit. The full Sidious set (1,686 chunks)
///         is the baseline.</item>
///   <item>Compares each candidate's <c>contentHash</c> against the
///         <c>kg.node_processed_chunks</c> ledger. Identical → skip
///         (counted as <c>chunksSkippedUnchanged</c>); otherwise → carried into
///         the new-chunks set bundled by Stage 2.</item>
/// </list>
///
/// Mirrors instance state into the checkpoint so a resumed run rehydrates without
/// re-running discovery — same pattern as <c>PageDiscoveryExecutor</c> in the
/// Character Timeline pipeline.
/// </summary>
internal sealed class HolocronContextDiscoveryExecutor : Executor<string, string>
{
    /// <summary>State scope used for both reads and writes by this executor.</summary>
    public const string Scope = "HolocronDiscovery";

    public const string KeyNode = "node";
    public const string KeyOutEdges = "outEdges";
    public const string KeyInEdges = "inEdges";
    public const string KeyNeighbours = "neighbours";
    public const string KeyCanonicalLabels = "canonicalLabels";
    public const string KeyOwnPageChunks = "ownPageChunks";
    public const string KeyNewChunks = "newChunks";
    public const string KeyDiscoveredCount = "discoveredCount";
    public const string KeySkippedCount = "skippedCount";

    /// <summary>
    /// Top-K edge labels surfaced to the agent. Same value as
    /// <c>HolocronAgent.BuildContextAsync</c> uses — the canonical-label registry
    /// is type-scoped, and 25 covers the long tail per type.
    /// </summary>
    const int CanonicalLabelTopK = 25;

    readonly IMongoClient _mongoClient;
    readonly SettingsOptions _settings;
    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly int _pageId;
    readonly string _jobId;
    readonly HolocronJobService _jobService;

    /// <summary>Number of total backlink chunks discovered (before skip-if-unchanged). Mirrored to checkpoint.</summary>
    public int DiscoveredCount { get; private set; }

    /// <summary>Number of chunks the ledger said were unchanged since last run. Mirrored to checkpoint.</summary>
    public int SkippedCount { get; private set; }

    public HolocronContextDiscoveryExecutor(
        IMongoClient mongoClient,
        SettingsOptions settings,
        ILogger logger,
        HolocronJobService jobService,
        int pageId,
        string jobId,
        HolocronEnhancementTracker? tracker
    )
        : base("HolocronContextDiscovery")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _jobService = jobService;
        _pageId = pageId;
        _jobId = jobId;
        _tracker = tracker;
    }

    IMongoCollection<GraphNode> Nodes => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    IMongoCollection<RelationshipEdge> Edges => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);
    IMongoCollection<ArticleChunk> Chunks => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<ArticleChunk>(Collections.SearchChunks);
    IMongoCollection<RelationshipLabel> Labels => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<RelationshipLabel>(Collections.KgLabels);

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.QueueStateUpdateAsync(KeyDiscoveredCount, DiscoveredCount, Scope, cancellationToken);
        await context.QueueStateUpdateAsync(KeySkippedCount, SkippedCount, Scope, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        DiscoveredCount = await context.ReadStateAsync<int>(KeyDiscoveredCount, Scope, cancellationToken);
        SkippedCount = await context.ReadStateAsync<int>(KeySkippedCount, Scope, cancellationToken);
        _logger.LogInformation("HolocronDiscovery restored: discovered={Discovered}, skipped={Skipped}", DiscoveredCount, SkippedCount);
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        // ── 1. Target node ─────────────────────────────────────────────────
        var node = await Nodes.Find(n => n.PageId == _pageId).FirstOrDefaultAsync(ct);
        if (node is null)
            throw new InvalidOperationException($"Holocron: node {_pageId} not found in kg.nodes — cannot enhance.");

        // Soft-warn on missing contentHash. ~0.05% of kg.nodes are stub records — pages
        // where the wiki download succeeded but the infobox parse produced no Data array
        // (e.g. raw.pages.infobox = `{Template: "..."}` only). Phase 1 can't hash an empty
        // infobox, so contentHash ends up null. Backlink chunks still exist for these
        // nodes, so the agent CAN still annotate edges — we just can't track Phase 1
        // staleness on resulting enrichments. The staleness sweep tolerates empty hashes
        // (see HolocronAgent.RunStalenessSweepAsync) so leaving the node hash empty here
        // is degraded but not destructive.
        if (string.IsNullOrEmpty(node.ContentHash))
        {
            _logger.LogWarning(
                "HolocronDiscovery: PageId={PageId} ({Name}) has no contentHash — Phase 1 stub (likely empty infobox). Proceeding with degraded staleness tracking.",
                _pageId,
                node.Name
            );
        }

        var nodeSnapshot = new HolocronNodeSnapshot(
            PageId: node.PageId,
            Name: node.Name,
            Type: node.Type,
            ContentHash: node.ContentHash ?? string.Empty,
            WikiUrl: node.WikiUrl ?? string.Empty,
            Continuity: node.Continuity,
            Realm: node.Realm,
            StartYear: node.StartYear,
            EndYear: node.EndYear,
            Properties: node.Properties,
            TemporalFacets: node.TemporalFacets.Select(f => new TemporalFacetSnapshot(f.Semantic, f.Calendar, f.Year, f.Text)).ToList()
        );

        _tracker?.UpdateProgress(_pageId, HolocronJobStatus.Discovering, $"Building context for {node.Name}...", currentStep: 1, totalSteps: 4, currentItem: node.Name);
        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Discovering, null, ct);

        // ── 2. Edges + neighbours ──────────────────────────────────────────
        var maxNeighbours = Math.Max(1, _settings.HolocronMaxNeighborsForContext);
        var halfLimit = Math.Max(1, maxNeighbours / 2);

        var outEdges = await Edges.Find(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, _pageId)).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);
        var inEdges = await Edges.Find(Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, _pageId)).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);

        var neighbourIds = outEdges.Select(e => e.ToId).Concat(inEdges.Select(e => e.FromId)).Distinct().ToList();
        var neighbours =
            neighbourIds.Count == 0
                ? new List<HolocronNeighbour>()
                : await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, neighbourIds)).Project(n => new HolocronNeighbour(n.PageId, n.Name, n.Type, n.StartYear, n.EndYear)).ToListAsync(ct);

        // ── 3. Canonical labels ────────────────────────────────────────────
        var labelsForType = await Labels.Find(Builders<RelationshipLabel>.Filter.AnyEq(l => l.FromTypes, node.Type)).SortByDescending(l => l.UsageCount).Limit(CanonicalLabelTopK).ToListAsync(ct);
        if (labelsForType.Count < 5)
        {
            // Sparse type — backfill from the overall top labels so the agent always
            // has a working vocabulary. Same fallback as HolocronAgent.BuildContextAsync.
            var overall = await Labels.Find(Builders<RelationshipLabel>.Filter.Empty).SortByDescending(l => l.UsageCount).Limit(CanonicalLabelTopK - labelsForType.Count).ToListAsync(ct);
            var seen = labelsForType.Select(l => l.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            labelsForType.AddRange(overall.Where(l => !seen.Contains(l.Label)));
        }
        var canonicalLabels = labelsForType.Select(l => new HolocronCanonicalLabel(l.Label, l.Reverse, l.Description, l.UsageCount)).ToList();

        // ── 4. Own-page chunks (grounding context for every batch) ─────────
        var ownLimit = Math.Max(0, _settings.HolocronOwnPageChunks);
        var ownChunks =
            ownLimit == 0
                ? new List<HolocronChunkPayload>()
                : await Chunks
                    .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, _pageId))
                    .SortBy(c => c.ChunkIndex)
                    .Limit(ownLimit)
                    .Project(c => new HolocronChunkPayload(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, c.ContentHash))
                    .ToListAsync(ct);

        _tracker?.UpdateProgress(_pageId, HolocronJobStatus.Discovering, $"Discovering backlink chunks for {node.Name}...", currentStep: 2, totalSteps: 4, currentItem: node.Name);

        // ── 5. Backlink chunks (full corpus, projected to refs) ────────────
        // Project DIRECTLY to HolocronChunkRef — text length is computed server-side
        // via $strLenCP so the heavy text body never crosses the wire here. Anakin's
        // 2,520 chunks → ~500 KB of refs (vs ~15 MB if we kept text), keeping the
        // workflow checkpoint comfortably under Mongo's 16 MB document limit.
        var backlinkChunks = new List<HolocronChunkRef>();
        if (!string.IsNullOrWhiteSpace(node.WikiUrl))
        {
            var backlinkFilter = Builders<ArticleChunk>.Filter.AnyEq(c => c.Links, node.WikiUrl) & Builders<ArticleChunk>.Filter.Ne(c => c.PageId, _pageId);
            backlinkChunks = await Chunks
                .Find(backlinkFilter)
                .SortBy(c => c.PageId)
                .ThenBy(c => c.ChunkIndex)
                .Project(c => new HolocronChunkRef(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.ContentHash, c.Text == null ? 0 : c.Text.Length))
                .ToListAsync(ct);
        }
        DiscoveredCount = backlinkChunks.Count;
        var linkingPages = backlinkChunks.Select(c => c.PageId).Distinct().Count();

        _logger.LogInformation("HolocronDiscovery: PageId={PageId} ({Name}) — {Total} backlink chunks across {Pages} linking pages.", _pageId, node.Name, DiscoveredCount, linkingPages);

        _tracker?.UpdateProgress(_pageId, HolocronJobStatus.Discovering, $"Comparing {DiscoveredCount} chunks against the processing ledger...", currentStep: 3, totalSteps: 4, currentItem: node.Name);

        // ── 6. Skip-if-unchanged ───────────────────────────────────────────
        var processedHashes = await _jobService.GetProcessedChunkHashesAsync(_pageId, ct);
        var newChunks = new List<HolocronChunkRef>(backlinkChunks.Count);
        SkippedCount = 0;
        foreach (var chunk in backlinkChunks)
        {
            if (
                processedHashes.TryGetValue(chunk.ChunkId, out var recordedHash)
                && !string.IsNullOrEmpty(chunk.ContentHash)
                && string.Equals(recordedHash, chunk.ContentHash, StringComparison.Ordinal)
            )
            {
                SkippedCount++;
            }
            else
            {
                newChunks.Add(chunk);
            }
        }

        // ── 7. Persist state for downstream executors ──────────────────────
        await context.QueueStateUpdateAsync(KeyNode, nodeSnapshot, Scope, ct);
        await context.QueueStateUpdateAsync(KeyOutEdges, outEdges, Scope, ct);
        await context.QueueStateUpdateAsync(KeyInEdges, inEdges, Scope, ct);
        await context.QueueStateUpdateAsync(KeyNeighbours, neighbours, Scope, ct);
        await context.QueueStateUpdateAsync(KeyCanonicalLabels, canonicalLabels, Scope, ct);
        await context.QueueStateUpdateAsync(KeyOwnPageChunks, ownChunks, Scope, ct);
        await context.QueueStateUpdateAsync(KeyNewChunks, newChunks, Scope, ct);
        await context.QueueStateUpdateAsync(KeyDiscoveredCount, DiscoveredCount, Scope, ct);
        await context.QueueStateUpdateAsync(KeySkippedCount, SkippedCount, Scope, ct);

        // ── 8. Update job doc + emit summary event ─────────────────────────
        await _jobService.TransitionAsync(
            _jobId,
            HolocronJobStatus.Discovering,
            Builders<HolocronJob>.Update.Set(j => j.ChunksDiscovered, DiscoveredCount).Set(j => j.ChunksProcessedNew, newChunks.Count).Set(j => j.ChunksSkippedUnchanged, SkippedCount),
            ct
        );

        await context.AddEventAsync(
            new HolocronDiscoveryCompleteEvent(
                new HolocronDiscoveryCompleteData(DiscoveredCount, newChunks.Count, SkippedCount, linkingPages, outEdges.Count, inEdges.Count, neighbours.Count, canonicalLabels.Count)
            ),
            ct
        );

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Discovering,
            newChunks.Count == 0
                ? $"All {SkippedCount} chunks unchanged — nothing to extract."
                : $"Discovered {DiscoveredCount} chunks, {newChunks.Count} new (changed since last run), {SkippedCount} unchanged.",
            currentStep: 4,
            totalSteps: 4,
            currentItem: node.Name
        );

        return $"Discovered {DiscoveredCount} chunks for {node.Name} ({newChunks.Count} new, {SkippedCount} unchanged)";
    }
}
