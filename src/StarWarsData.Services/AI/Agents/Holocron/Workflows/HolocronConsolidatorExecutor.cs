using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 4 of the Holocron workflow (Design-020). Pure C#, no LLM. Reads the
/// accumulated raw proposals from every batch and reduces them to a clean,
/// dedup-ed, pre-flight-validated set ready for the apply step.
///
/// Two layers:
///
/// <list type="number">
///   <item><b>Cross-batch dedup</b> — overlapping batches will frequently produce
///         the same Annotate edge (a famous relationship gets discussed on many
///         backlinking pages). We collapse on key:
///         <list type="bullet">
///           <item><c>annotateEdges</c> by <c>(fromId, toId, label)</c> — keep the
///                 one with most evidence, breaking ties by longest description.</item>
///           <item><c>fillGapEdges</c> by <c>(fromId, toId, label)</c> — keep the
///                 one with the most non-null bounds.</item>
///           <item><c>addEdges</c> by unordered <c>NodePairKey</c> — only one
///                 brand-new edge per pair survives, keep the one with most evidence.</item>
///           <item><c>nodeProposals</c> by <c>fieldPath</c> — union all proposed
///                 values, keep the richest claim/reasoning.</item>
///         </list>
///   </item>
///   <item><b>Pre-flight validation</b> — same rules <c>HolocronAgent.ApplyProposalsAsync</c>
///         enforces: every proposal must cite at least one valid source (PageId
///         in <c>kg.nodes</c> or chunkId in <c>search.chunks</c>); Annotate +
///         FillGap target tuples must exist as edges; Add labels must be in the
///         canonical registry and the pair must have no existing connection.</item>
/// </list>
///
/// Counters (DuplicatesDropped + PreflightRejects + EvidenceFailures) flow into
/// the job-doc summary and the activity log so the user can see why N raw
/// proposals collapsed to M applied enrichments.
/// </summary>
internal sealed class HolocronConsolidatorExecutor : Executor<string, string>
{
    public const string Scope = "HolocronConsolidation";
    public const string KeyConsolidated = "consolidated";

    readonly IMongoClient _mongoClient;
    readonly SettingsOptions _settings;
    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly HolocronJobService _jobService;
    readonly int _pageId;
    readonly string _jobId;

    /// <summary>
    /// Canonical-label set built once per run. Pre-flight rejects any
    /// <c>addEdges</c> proposal whose label isn't here, mirroring the synchronous
    /// <c>HolocronAgent</c> path.
    /// </summary>
    readonly HashSet<string> _knownLabels;

    public HolocronConsolidatorExecutor(
        IMongoClient mongoClient,
        SettingsOptions settings,
        ILogger logger,
        HolocronJobService jobService,
        int pageId,
        string jobId,
        HolocronEnhancementTracker? tracker
    )
        : base("HolocronConsolidator")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _jobService = jobService;
        _pageId = pageId;
        _jobId = jobId;
        _tracker = tracker;
        _knownLabels = InfoboxDefinitionRegistry.AllLabelDefinitions().Select(d => d.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    IMongoCollection<GraphNode> Nodes => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    IMongoCollection<ArticleChunk> Chunks => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<ArticleChunk>(Collections.SearchChunks);
    IMongoCollection<EdgeEnrichment> EdgeEnrichments => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var raw =
            await context.ReadStateAsync<HolocronRawProposalSet>(HolocronProposalExtractorExecutor.KeyRawProposals, HolocronProposalExtractorExecutor.Scope, ct)
            ?? new HolocronRawProposalSet([], [], [], []);
        var node =
            await context.ReadStateAsync<HolocronNodeSnapshot>(HolocronContextDiscoveryExecutor.KeyNode, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronConsolidator: no node in Discovery state");
        var outEdges = await context.ReadStateAsync<List<RelationshipEdge>>(HolocronContextDiscoveryExecutor.KeyOutEdges, HolocronContextDiscoveryExecutor.Scope, ct) ?? [];
        var inEdges = await context.ReadStateAsync<List<RelationshipEdge>>(HolocronContextDiscoveryExecutor.KeyInEdges, HolocronContextDiscoveryExecutor.Scope, ct) ?? [];

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Consolidating, null, ct);

        var rawTotal = raw.AnnotateEdges.Count + raw.FillGapEdges.Count + raw.AddEdges.Count + raw.NodeProposals.Count;
        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Consolidating,
            $"Consolidating {rawTotal} raw proposals...",
            currentStep: 0,
            totalSteps: 1,
            currentItem: node.Name,
            proposalsExtracted: rawTotal
        );

        // ── 1. Cross-batch dedup (with evidence merging) ───────────────────
        // High-degree nodes like Anakin produce hundreds of duplicate proposals for the
        // same `(fromId, toId, label)` — each batch sees a different chunk supporting the
        // same claim. Naive "keep one, drop the rest" loses 99% of the unique evidence
        // excerpts. Instead we pick the canonical proposal (richest narrative) but UNION
        // the evidence arrays from every duplicate, deduped by chunkId / sourcePageId.
        // The applied enrichment then carries the full evidence corpus.
        var dedupedAnnotates = raw
            .AnnotateEdges.GroupBy(p => $"{p.FromId}-{p.ToId}-{p.Label.ToLowerInvariant()}")
            .Select(g =>
            {
                var canonical = g.OrderByDescending(p => p.Evidence.Count).ThenByDescending(p => p.Description?.Length ?? 0).First();
                return canonical with { Evidence = MergeEvidence(g.SelectMany(p => p.Evidence)) };
            })
            .ToList();

        var dedupedFillGaps = raw
            .FillGapEdges.GroupBy(p => $"{p.FromId}-{p.ToId}-{p.Label.ToLowerInvariant()}")
            .Select(g =>
            {
                var canonical = g.OrderByDescending(p => (p.FromYear.HasValue ? 1 : 0) + (p.ToYear.HasValue ? 1 : 0)).ThenByDescending(p => p.Evidence.Count).First();
                return canonical with { Evidence = MergeEvidence(g.SelectMany(p => p.Evidence)) };
            })
            .ToList();

        var dedupedAddEdges = raw
            .AddEdges.GroupBy(p => NodePairKey(p.FromId, p.ToId))
            .Select(g =>
            {
                var canonical = g.OrderByDescending(p => p.Evidence.Count).ThenByDescending(p => p.Reasoning?.Length ?? 0).First();
                return canonical with { Evidence = MergeEvidence(g.SelectMany(p => p.Evidence)) };
            })
            .ToList();

        // Node proposals: union values per fieldPath, keep richest claim+reasoning + merged evidence.
        var dedupedNodeProps = raw
            .NodeProposals.GroupBy(p => StripPropertiesPrefix(p.FieldPath))
            .Select(g =>
            {
                var unionedValues = g.SelectMany(p => p.Values).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var richest = g.OrderByDescending(p => p.Reasoning?.Length ?? 0).First();
                var mergedEvidence = g.SelectMany(p => p.Evidence).Distinct().ToList();
                return new HolocronNodeProposalPayload(richest.BatchIndex, g.Key, unionedValues, richest.Claim, mergedEvidence, richest.Reasoning);
            })
            .ToList();

        var duplicatesDropped =
            (raw.AnnotateEdges.Count - dedupedAnnotates.Count)
            + (raw.FillGapEdges.Count - dedupedFillGaps.Count)
            + (raw.AddEdges.Count - dedupedAddEdges.Count)
            + (raw.NodeProposals.Count - dedupedNodeProps.Count);

        // ── 2. Pre-flight validation ───────────────────────────────────────
        // (a) Evidence universe — single bulk query for all cited pages + chunks.
        var allEvidence = dedupedAnnotates
            .SelectMany(p => p.Evidence)
            .Concat(dedupedFillGaps.SelectMany(p => p.Evidence))
            .Concat(dedupedAddEdges.SelectMany(p => p.Evidence))
            .Concat(dedupedNodeProps.SelectMany(p => p.Evidence))
            .ToList();

        var citedPageIds = allEvidence.Where(e => e.SourcePageId is { } pid && pid > 0).Select(e => e.SourcePageId!.Value).Distinct().ToList();
        var citedChunkIds = allEvidence.Where(e => !string.IsNullOrEmpty(e.ChunkId) && ObjectId.TryParse(e.ChunkId, out _)).Select(e => e.ChunkId!).Distinct().ToList();

        var validPageIds =
            citedPageIds.Count == 0 ? new HashSet<int>() : new HashSet<int>(await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, citedPageIds)).Project(n => n.PageId).ToListAsync(ct));
        var validChunkIds =
            citedChunkIds.Count == 0 ? new HashSet<string>() : new HashSet<string>(await Chunks.Find(Builders<ArticleChunk>.Filter.In(c => c.Id, citedChunkIds)).Project(c => c.Id).ToListAsync(ct));

        // (b) Edges already on the graph — keys for Annotate/FillGap target lookup
        // and unordered-pair set for Add rejection. Mirrors HolocronAgent.ApplyProposalsAsync.
        var existingEdges = outEdges.Concat(inEdges).ToList();
        var existingEdgeKeys = existingEdges.Select(e => $"{e.FromId}-{e.ToId}-{e.Label.ToLowerInvariant()}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNodePairs = existingEdges.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // (c) Active edge enrichments touching the target — Add proposals must avoid these too.
        var activeEdgeEnrichments = await EdgeEnrichments
            .Find(
                Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active)
                    & (Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, _pageId) | Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, _pageId))
            )
            .ToListAsync(ct);
        var enrichmentNodePairs = activeEdgeEnrichments.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var preflightRejects = 0;
        var evidenceFailures = 0;

        var survivingAnnotates = new List<HolocronAnnotateProposal>();
        foreach (var p in dedupedAnnotates)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsAnnotateValid(p, existingEdgeKeys))
            {
                preflightRejects++;
                continue;
            }
            survivingAnnotates.Add(p);
        }

        var survivingFillGaps = new List<HolocronFillGapProposal>();
        foreach (var p in dedupedFillGaps)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsFillGapValid(p, existingEdges))
            {
                preflightRejects++;
                continue;
            }
            survivingFillGaps.Add(p);
        }

        var survivingAddEdges = new List<HolocronAddEdgeProposal>();
        foreach (var p in dedupedAddEdges)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsAddEdgeValid(p, existingNodePairs, enrichmentNodePairs))
            {
                preflightRejects++;
                continue;
            }
            survivingAddEdges.Add(p);
        }

        var survivingNodeProps = new List<HolocronNodeProposalPayload>();
        foreach (var p in dedupedNodeProps)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.FieldPath) || p.Values.Count == 0)
            {
                preflightRejects++;
                continue;
            }
            survivingNodeProps.Add(p);
        }

        var consolidated = new HolocronConsolidatedProposals(survivingAnnotates, survivingFillGaps, survivingAddEdges, survivingNodeProps, duplicatesDropped, preflightRejects, evidenceFailures);

        await context.QueueStateUpdateAsync(KeyConsolidated, consolidated, Scope, ct);

        await _jobService.TransitionAsync(
            _jobId,
            HolocronJobStatus.Consolidating,
            Builders<HolocronJob>.Update.Set(j => j.DuplicatesDropped, duplicatesDropped).Set(j => j.PreflightRejects, preflightRejects).Set(j => j.EvidenceFailures, evidenceFailures),
            ct
        );

        var consolidatedTotal = survivingAnnotates.Count + survivingFillGaps.Count + survivingAddEdges.Count + survivingNodeProps.Count;
        await context.AddEventAsync(
            new HolocronConsolidationCompleteEvent(new HolocronConsolidationCompleteData(rawTotal, consolidatedTotal, duplicatesDropped, preflightRejects, evidenceFailures)),
            ct
        );

        _logger.LogInformation(
            "HolocronConsolidator: PageId={PageId} ({Name}) — {Raw} raw → {Final} survivors ({Dup} dup-dropped, {Pre} pre-flight-rejected, {Ev} evidence-failed).",
            _pageId,
            node.Name,
            rawTotal,
            consolidatedTotal,
            duplicatesDropped,
            preflightRejects,
            evidenceFailures
        );

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Consolidating,
            $"Consolidated {rawTotal} raw → {consolidatedTotal} survivors ({duplicatesDropped} dup, {preflightRejects} rejected, {evidenceFailures} no-evidence)",
            currentStep: 1,
            totalSteps: 1,
            currentItem: node.Name,
            proposalsExtracted: rawTotal,
            enrichmentsApplied: consolidatedTotal
        );

        return $"Consolidated {rawTotal} → {consolidatedTotal} for {node.Name}";
    }

    // ── Pre-flight helpers (mirror HolocronAgent.ApplyProposalsAsync's checks) ──

    static bool HasValidEvidence(List<HolocronEvidencePayload> evidence, HashSet<int> validPageIds, HashSet<string> validChunkIds)
    {
        foreach (var ev in evidence)
        {
            if (ev.SourcePageId is { } pid && pid > 0 && validPageIds.Contains(pid))
                return true;
            if (!string.IsNullOrEmpty(ev.ChunkId) && validChunkIds.Contains(ev.ChunkId))
                return true;
        }
        return false;
    }

    bool IsAnnotateValid(HolocronAnnotateProposal p, HashSet<string> existingEdgeKeys)
    {
        if (p.FromId <= 0 || p.ToId <= 0 || string.IsNullOrWhiteSpace(p.Label))
            return false;
        if (p.FromId != _pageId && p.ToId != _pageId)
            return false;
        if (!existingEdgeKeys.Contains($"{p.FromId}-{p.ToId}-{p.Label.ToLowerInvariant()}"))
            return false;
        return !string.IsNullOrWhiteSpace(p.Role) || !string.IsNullOrWhiteSpace(p.Qualifier) || !string.IsNullOrWhiteSpace(p.Description);
    }

    bool IsFillGapValid(HolocronFillGapProposal p, List<RelationshipEdge> existingEdges)
    {
        if (p.FromId <= 0 || p.ToId <= 0 || string.IsNullOrWhiteSpace(p.Label))
            return false;
        if (p.FromId != _pageId && p.ToId != _pageId)
            return false;
        if (!p.FromYear.HasValue && !p.ToYear.HasValue)
            return false;
        // Per Design-021: a proposed bound is applicable if the matching edge's existing
        // bound is either null OR explicitly Lifecycle-tagged (Phase 5 lifecycle-fallback,
        // soft upper bound, refinable). Untagged (Unknown) and Infobox bounds are hard.
        // This must mirror HolocronAgent.IsBoundRefinable so the consolidator's pre-flight
        // doesn't reject what the agent path would accept.
        return existingEdges.Any(e =>
            e.FromId == p.FromId
            && e.ToId == p.ToId
            && string.Equals(e.Label, p.Label, StringComparison.OrdinalIgnoreCase)
            && ((p.FromYear.HasValue && IsBoundRefinable(e, isFrom: true)) || (p.ToYear.HasValue && IsBoundRefinable(e, isFrom: false)))
        );
    }

    /// <summary>
    /// Mirrors <c>HolocronAgent.IsBoundRefinable</c> (Design-021). A bound is refinable
    /// when it's null, or when it carries an explicit <see cref="EdgeBoundsSource.Lifecycle"/>
    /// tag (Phase 5 fallback, soft upper bound). Untagged (Unknown) and Infobox bounds are
    /// treated as hard and are not touched by FillGap.
    /// </summary>
    static bool IsBoundRefinable(RelationshipEdge edge, bool isFrom)
    {
        var existing = isFrom ? edge.FromYear : edge.ToYear;
        if (!existing.HasValue)
            return true;
        var src = edge.Meta?.BoundsSource ?? EdgeBoundsSource.Unknown;
        return src is EdgeBoundsSource.Lifecycle;
    }

    bool IsAddEdgeValid(HolocronAddEdgeProposal p, HashSet<string> existingNodePairs, HashSet<string> enrichmentNodePairs)
    {
        if (p.FromId <= 0 || p.ToId <= 0 || string.IsNullOrWhiteSpace(p.Label))
            return false;
        if (p.FromId != _pageId && p.ToId != _pageId)
            return false;
        if (!_knownLabels.Contains(p.Label))
            return false;
        var pair = NodePairKey(p.FromId, p.ToId);
        return !existingNodePairs.Contains(pair) && !enrichmentNodePairs.Contains(pair);
    }

    static string NodePairKey(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

    /// <summary>
    /// Merge evidence lists from multiple "duplicate" proposals into a single deduped
    /// list. Dedup key: chunkId when present (most evidence has one), else
    /// <c>"page:{sourcePageId}"</c>. This way each surviving enrichment carries the
    /// union of every chunk that supported the claim across all batches — for a
    /// high-degree node like Anakin, "affiliated_with → Jedi Order" lands with the
    /// evidence from ~169 distinct backlink chunks instead of just the canonical one.
    /// </summary>
    static List<HolocronEvidencePayload> MergeEvidence(IEnumerable<HolocronEvidencePayload> evidence) =>
        evidence.GroupBy(e => !string.IsNullOrEmpty(e.ChunkId) ? $"chunk:{e.ChunkId}" : $"page:{e.SourcePageId ?? 0}").Select(g => g.First()).ToList();

    static string StripPropertiesPrefix(string fieldPath) =>
        string.IsNullOrEmpty(fieldPath) ? fieldPath
        : fieldPath.StartsWith("properties.", StringComparison.OrdinalIgnoreCase) ? fieldPath["properties.".Length..]
        : fieldPath;
}
