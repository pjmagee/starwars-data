using System.Text.RegularExpressions;
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
    readonly Dictionary<string, HashSet<string>> _expectedTargetsByLabel;
    readonly HolocronAuditService _audit;

    public HolocronConsolidatorExecutor(
        IMongoClient mongoClient,
        SettingsOptions settings,
        ILogger logger,
        HolocronJobService jobService,
        HolocronAuditService audit,
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
        _audit = audit;
        _pageId = pageId;
        _jobId = jobId;
        _tracker = tracker;
        _knownLabels = InfoboxDefinitionRegistry.AllLabelDefinitions().Select(d => d.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Build a label → expected-target-types lookup so AddEdge validation can
        // reject proposals where the actual target's type isn't in the declared
        // Targets array (e.g. has_role with declared Targets=[TitleOrPosition]
        // must reject `has_role → Darth Sidious` because Sidious is a Character).
        // Multiple definitions may map to the same forward label (e.g. several
        // Affiliation* fields all → affiliated_with); union their Targets so we
        // accept any declared target type for that label.
        _expectedTargetsByLabel = InfoboxDefinitionRegistry
            .AllLabelDefinitions()
            .GroupBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(d => d.ExpectedTargetTypes).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    IMongoCollection<GraphNode> Nodes => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    IMongoCollection<ArticleChunk> Chunks => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<ArticleChunk>(Collections.SearchChunks);
    IMongoCollection<EdgeEnrichment> EdgeEnrichments => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
    IMongoCollection<RelationshipEdge> Edges => _mongoClient.GetDatabase(_settings.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    /// <summary>
    /// Hedge-word pattern surfacing the "I'm-not-actually-sure" tells the agent
    /// produces when it's inferring a relationship from a co-appearance / cast
    /// credit / link aggregation rather than a chunk that states the relationship.
    /// Anakin / Asajj / Ahsoka runs (2026-04-29) catalogued these phrasings in real
    /// hallucinations: "appears alongside Sidious in canon episode credits", "no
    /// add edge is warranted", "described among bounty hunters in an appearances
    /// context", "not supported strongly enough to add as a species edge", "if
    /// supported by the source chunks". The system prompt already tells the agent
    /// to skip; this regex is the post-hoc safety net for the calls that slip past.
    /// Word boundaries kept loose because the surrounding prose varies.
    /// </summary>
    static readonly Regex HallucinationPattern = new(
        @"appears? alongside|appears? with|appearances? context|appearance listings?|appearances? index|"
            + @"linked entity list|linked from pages|as a linked entity|as a distinct linked entity|"
            + @"no add edge is warranted|not warranted|not supported strongly|if supported by|if the source supports",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    static bool IsHedged(params string?[] texts)
    {
        foreach (var t in texts)
        {
            if (!string.IsNullOrEmpty(t) && HallucinationPattern.IsMatch(t))
                return true;
        }
        return false;
    }

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
        //
        // ⚠ outEdges / inEdges are top-K-by-weight from the discovery executor (typical K=5
        // each). For high-degree nodes (Ahsoka has 231 Phase 1 edges; Anakin has thousands)
        // the curated context misses most pairs. The Ahsoka v1.3.0 run staged duplicate Add
        // edges for `species → Togruta` and `serves_in → 501st Legion` because both pairs
        // already had Phase 1 edges that didn't make the top-K cut and so weren't in
        // existingNodePairs. To make the F5 (Add-on-existing-pair) check authoritative we
        // also query kg.edges for the full set of pairs touching _pageId.
        var existingEdges = outEdges.Concat(inEdges).ToList();
        var existingEdgeKeys = existingEdges.Select(e => $"{e.FromId}-{e.ToId}-{e.Label.ToLowerInvariant()}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNodePairs = existingEdges.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allTargetEdges = await Edges
            .Find(Builders<RelationshipEdge>.Filter.Or(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, _pageId), Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, _pageId)))
            .Project(e => new { e.FromId, e.ToId })
            .ToListAsync(ct);
        foreach (var e in allTargetEdges)
            existingNodePairs.Add(NodePairKey(e.FromId, e.ToId));

        // (c) Active edge enrichments touching the target — Add proposals must avoid these too.
        var activeEdgeEnrichments = await EdgeEnrichments
            .Find(
                Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active)
                    & (Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, _pageId) | Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, _pageId))
            )
            .ToListAsync(ct);
        var enrichmentNodePairs = activeEdgeEnrichments.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Template-scoped allowed-properties set. The target node's Type is the
        // template name; intersect with FieldSemantics.Properties so Phase E only
        // accepts properties that are actually valid for THIS template. Without
        // this, the agent could (and did) propose `Primary role(s)` (a Starship
        // free-text field) on a Character page just because the global Properties
        // flat-set contained it. ForTemplate(...) returns a per-template
        // InfoboxDefinition; .Properties is the intersection we want.
        var allowedProperties = InfoboxDefinitionRegistry.ForTemplate(node.Type).Properties;

        var preflightRejects = 0;
        var evidenceFailures = 0;
        var auditIds = new Dictionary<string, string>(StringComparer.Ordinal);

        var survivingAnnotates = new List<HolocronAnnotateProposal>();
        foreach (var p in dedupedAnnotates)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                await RecordAnnotateAuditAsync(p, "rejected_evidence", "Cited chunkId / sourcePageId not found", ct);
                continue;
            }
            if (IsHedged(p.Claim, p.Reasoning, p.Description))
            {
                preflightRejects++;
                await RecordAnnotateAuditAsync(p, "rejected_preflight_hedge", "Claim or reasoning contained hedge-word pattern (HolocronConsolidator.HallucinationPattern)", ct);
                continue;
            }
            if (!IsAnnotateValid(p, existingEdgeKeys))
            {
                preflightRejects++;
                await RecordAnnotateAuditAsync(p, "rejected_preflight_other", "Failed IsAnnotateValid: missing target edge / fromId / label / context fields", ct);
                continue;
            }
            var auditId = await RecordAnnotateAuditAsync(p, "pending_verifier", string.Empty, ct);
            auditIds[KeyAnnotate(p.FromId, p.ToId, p.Label)] = auditId;
            survivingAnnotates.Add(p);
        }

        var survivingFillGaps = new List<HolocronFillGapProposal>();
        foreach (var p in dedupedFillGaps)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                await RecordFillGapAuditAsync(p, "rejected_evidence", "Cited chunkId / sourcePageId not found", ct);
                continue;
            }
            if (IsHedged(p.Claim, p.Reasoning))
            {
                preflightRejects++;
                await RecordFillGapAuditAsync(p, "rejected_preflight_hedge", "Claim or reasoning contained hedge-word pattern", ct);
                continue;
            }
            if (!IsFillGapValid(p, existingEdges))
            {
                preflightRejects++;
                await RecordFillGapAuditAsync(p, "rejected_preflight_other", "Failed IsFillGapValid: target edge missing or bound not refinable per Design-021", ct);
                continue;
            }
            var auditId = await RecordFillGapAuditAsync(p, "pending_verifier", string.Empty, ct);
            auditIds[KeyFillGap(p.FromId, p.ToId, p.Label)] = auditId;
            survivingFillGaps.Add(p);
        }

        // Resolve target PageId → Type once for every Add proposal so we can enforce
        // the canonical label's declared Targets (e.g. has_role must point at a
        // TitleOrPosition node; if the agent picked a Character target the proposal
        // is rejected). Single bulk lookup against kg.nodes — cheap.
        var addEdgeTargetIds = dedupedAddEdges.Select(p => p.ToId).Where(id => id > 0).Distinct().ToList();
        var addEdgeTargetTypes =
            addEdgeTargetIds.Count == 0
                ? new Dictionary<int, string>()
                : (await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, addEdgeTargetIds)).Project(n => new { n.PageId, n.Type }).ToListAsync(ct)).ToDictionary(
                    n => n.PageId,
                    n => n.Type ?? string.Empty
                );

        var survivingAddEdges = new List<HolocronAddEdgeProposal>();
        foreach (var p in dedupedAddEdges)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                await RecordAddAuditAsync(p, "rejected_evidence", "Cited chunkId / sourcePageId not found", ct);
                continue;
            }
            if (IsHedged(p.Claim, p.Reasoning))
            {
                preflightRejects++;
                await RecordAddAuditAsync(p, "rejected_preflight_hedge", "Claim or reasoning contained hedge-word pattern", ct);
                continue;
            }
            if (!IsAddEdgeValid(p, existingNodePairs, enrichmentNodePairs, addEdgeTargetTypes))
            {
                preflightRejects++;
                var targetType = addEdgeTargetTypes.GetValueOrDefault(p.ToId, string.Empty);
                var reason = AddEdgeRejectReason(p, existingNodePairs, enrichmentNodePairs, targetType);
                await RecordAddAuditAsync(p, "rejected_preflight_" + reason.code, reason.message, ct);
                continue;
            }
            var auditId = await RecordAddAuditAsync(p, "pending_verifier", string.Empty, ct);
            auditIds[KeyAdd(p.FromId, p.ToId, p.Label)] = auditId;
            survivingAddEdges.Add(p);
        }

        var survivingNodeProps = new List<HolocronNodeProposalPayload>();
        foreach (var p in dedupedNodeProps)
        {
            if (!HasValidEvidence(p.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                await RecordPropertyAuditAsync(p, "rejected_evidence", "Cited chunkId / sourcePageId not found", ct);
                continue;
            }
            if (IsHedged(p.Claim, p.Reasoning))
            {
                preflightRejects++;
                await RecordPropertyAuditAsync(p, "rejected_preflight_hedge", "Claim or reasoning contained hedge-word pattern", ct);
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.FieldPath) || p.Values.Count == 0)
            {
                preflightRejects++;
                await RecordPropertyAuditAsync(p, "rejected_preflight_other", "Empty fieldPath or no values", ct);
                continue;
            }
            if (!allowedProperties.Contains(p.FieldPath))
            {
                preflightRejects++;
                await RecordPropertyAuditAsync(p, "rejected_preflight_fieldpath", $"fieldPath '{p.FieldPath}' not in template '{node.Type}' allow-list", ct);
                continue;
            }
            var auditId = await RecordPropertyAuditAsync(p, "pending_verifier", string.Empty, ct);
            auditIds[KeyProperty(p.FieldPath)] = auditId;
            survivingNodeProps.Add(p);
        }

        // ── Phase G: cross-vector dedup ───────────────────────────────────────
        // A single fact must be encoded ONCE in its strongest form. If the agent
        // proposed an edge to "Bounty hunter" AND a property `Occupation: bounty
        // hunter`, the edge wins; the property is the redundant duplicate. We
        // walk surviving edges to collect target-node names, then drop any
        // nodeProposal value that case-insensitively matches an edge target's
        // name. If a proposal's values list empties out, drop the proposal.
        //
        // Phase G also applies an Aliases-specific blocklist: Aliases must be
        // alternate proper-noun NAMES, never role/title/faction strings. We
        // resolve each Aliases value against kg.nodes; any value that exists
        // as a TitleOrPosition / Government / Organization / Religion / Species
        // / MilitaryUnit node is rejected — those are entities, not aliases.
        var allEdgeTargetIds = survivingAnnotates
            .Select(p => p.ToId)
            .Concat(survivingFillGaps.Select(p => p.ToId))
            .Concat(survivingAddEdges.Select(p => p.ToId))
            .Concat(survivingAnnotates.Select(p => p.FromId))
            .Concat(survivingFillGaps.Select(p => p.FromId))
            .Concat(survivingAddEdges.Select(p => p.FromId))
            .Where(id => id > 0 && id != _pageId)
            .Distinct()
            .ToList();
        var edgeTargetNames =
            allEdgeTargetIds.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, allEdgeTargetIds)).Project(n => n.Name).ToListAsync(ct))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Universal property-value blocklist — extends the v1.3.0 Aliases-only check
        // to every property fieldPath. Background: the Ahsoka v1.4.0 run produced
        // a `Titles` Augment containing 5 values that resolve to TitleOrPosition /
        // Religion nodes ("Jedi General", "Padawan", "Jedi Knight", "Jedi", "Fulcrum").
        // The Aliases check would have dropped these; Titles, Occupation, Primary
        // role(s) etc. let them through. Generalising means: any property value that
        // already exists as a node of a blocked type is encoded as an edge instead,
        // not a property string. False positives (legit codenames that happen to be
        // TitleOrPosition nodes — "Fulcrum") get dropped here; that's accepted.
        var propertyValuesToCheck = survivingNodeProps.SelectMany(p => p.Values).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Types that disqualify a string value from appearing as a property — the
        // string IS another entity in the graph, so the proposal should have been
        // an edge. Below-threshold types ("CulturalGroup", "FanOrganization") use
        // string literals because KgNodeTypes only covers types with ≥100 nodes.
        var nodeBlockedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            KgNodeTypes.TitleOrPosition,
            KgNodeTypes.Government,
            KgNodeTypes.Organization,
            KgNodeTypes.Religion,
            KgNodeTypes.Species,
            KgNodeTypes.MilitaryUnit,
            KgNodeTypes.Family,
            "CulturalGroup",
            "FanOrganization",
        };
        var nodeBlocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (propertyValuesToCheck.Count > 0)
        {
            var matches = await Nodes
                .Find(Builders<GraphNode>.Filter.In(n => n.Name, propertyValuesToCheck) & Builders<GraphNode>.Filter.In(n => n.Type, nodeBlockedTypes))
                .Project(n => n.Name)
                .ToListAsync(ct);
            foreach (var n in matches)
                if (!string.IsNullOrWhiteSpace(n))
                    nodeBlocklist.Add(n);
        }

        var crossVectorDropped = 0;
        var dedupedSurvivingNodeProps = new List<HolocronNodeProposalPayload>();
        foreach (var p in survivingNodeProps)
        {
            var keptValues = p
                .Values.Where(v =>
                {
                    if (string.IsNullOrWhiteSpace(v))
                        return false;
                    if (edgeTargetNames.Contains(v))
                        return false;
                    if (nodeBlocklist.Contains(v))
                        return false;
                    return true;
                })
                .ToList();
            var droppedValues = p.Values.Count - keptValues.Count;
            if (keptValues.Count == 0)
            {
                crossVectorDropped++;
                continue;
            }
            if (droppedValues > 0)
                dedupedSurvivingNodeProps.Add(p with { Values = keptValues });
            else
                dedupedSurvivingNodeProps.Add(p);
        }
        preflightRejects += crossVectorDropped;
        survivingNodeProps = dedupedSurvivingNodeProps;

        // Reconcile auditIds with the post-Phase-G survivor set: any row that was originally
        // pending_verifier but got dropped by Phase G (cross-vector dedup or universal-property
        // blocklist) needs its audit outcome updated to rejected_preflight_blocklist.
        var survivorPropertyKeys = survivingNodeProps.Select(p => KeyProperty(p.FieldPath)).ToHashSet(StringComparer.Ordinal);
        var droppedKeys = auditIds.Keys.Where(k => k.StartsWith("property|", StringComparison.Ordinal) && !survivorPropertyKeys.Contains(k)).ToList();
        foreach (var key in droppedKeys)
        {
            await _audit.UpdateOutcomeAsync(
                auditIds[key],
                stage: "consolidation_phaseG",
                outcome: "rejected_preflight_blocklist",
                reason: "Property value matched edge-target name or resolved to a blocked-type node (TitleOrPosition / Government / Organization / Religion / Species / MilitaryUnit / Family / CulturalGroup / FanOrganization)",
                ct: ct
            );
            auditIds.Remove(key);
        }

        var consolidated = new HolocronConsolidatedProposals(
            survivingAnnotates,
            survivingFillGaps,
            survivingAddEdges,
            survivingNodeProps,
            duplicatesDropped,
            preflightRejects,
            evidenceFailures,
            auditIds
        );

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

    bool IsAddEdgeValid(HolocronAddEdgeProposal p, HashSet<string> existingNodePairs, HashSet<string> enrichmentNodePairs, IReadOnlyDictionary<int, string> targetTypes)
    {
        if (p.FromId <= 0 || p.ToId <= 0 || string.IsNullOrWhiteSpace(p.Label))
            return false;
        if (p.FromId != _pageId && p.ToId != _pageId)
            return false;
        if (!_knownLabels.Contains(p.Label))
            return false;

        // Type-constraint: if the canonical label declares specific target types, the
        // actual target must match one of them. Catches `has_role → Character` and
        // similar agent confusions where the label semantically demands a particular
        // node type but the agent grabbed a wrong-typed entity from the linked-entities
        // hint section. When Targets is empty (rare), skip the check — historical
        // labels without declared Targets are permissive by design.
        if (_expectedTargetsByLabel.TryGetValue(p.Label, out var expected) && expected.Count > 0)
        {
            var targetType = targetTypes.GetValueOrDefault(p.ToId, string.Empty);
            if (string.IsNullOrEmpty(targetType) || !expected.Contains(targetType))
                return false;
        }

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

    // ── Audit helpers ──────────────────────────────────────────────────────

    static string KeyAnnotate(int from, int to, string label) => $"annotate|{from}|{to}|{label.ToLowerInvariant()}";

    static string KeyFillGap(int from, int to, string label) => $"fillgap|{from}|{to}|{label.ToLowerInvariant()}";

    static string KeyAdd(int from, int to, string label) => $"add|{from}|{to}|{label.ToLowerInvariant()}";

    static string KeyProperty(string fieldPath) => $"property|{fieldPath}";

    (string code, string message) AddEdgeRejectReason(HolocronAddEdgeProposal p, HashSet<string> existingNodePairs, HashSet<string> enrichmentNodePairs, string targetType)
    {
        if (p.FromId <= 0 || p.ToId <= 0 || string.IsNullOrWhiteSpace(p.Label))
            return ("other", "Invalid fromId / toId / label");
        if (p.FromId != _pageId && p.ToId != _pageId)
            return ("other", "Pair does not include focal node");
        if (!_knownLabels.Contains(p.Label))
            return ("other", $"Label '{p.Label}' not in canonical FieldSemantics vocabulary");
        // Type-mismatch first — most actionable signal when the label has declared targets.
        if (_expectedTargetsByLabel.TryGetValue(p.Label, out var expected) && expected.Count > 0 && !string.IsNullOrEmpty(targetType) && !expected.Contains(targetType))
            return ("target_type", $"Target node type '{targetType}' not in label '{p.Label}' ExpectedTargetTypes [{string.Join(", ", expected)}]");
        var pair = NodePairKey(p.FromId, p.ToId);
        if (existingNodePairs.Contains(pair))
            return ("dup_pair", "Pair already has a Phase 1 / cached edge — would create a parallel relationship");
        if (enrichmentNodePairs.Contains(pair))
            return ("dup_pair", "Pair already has an active Holocron edge enrichment");
        return ("other", "Failed IsAddEdgeValid for an unclassified reason");
    }

    Task<string> RecordAnnotateAuditAsync(HolocronAnnotateProposal p, string outcome, string reason, CancellationToken ct) =>
        _audit.RecordAsync(
            new HolocronAudit
            {
                JobId = _jobId,
                PageId = _pageId,
                Kind = "annotate",
                FromId = p.FromId,
                ToId = p.ToId,
                Label = p.Label,
                Claim = p.Claim ?? string.Empty,
                Reasoning = p.Reasoning,
                Evidence = p.Evidence?.Select(BuildAuditEvidence).ToList() ?? [],
                Outcome = outcome,
                OutcomeReason = string.IsNullOrEmpty(reason) ? null : reason,
                AgentVersion = HolocronAgent.AgentVersion,
            },
            ct
        );

    Task<string> RecordFillGapAuditAsync(HolocronFillGapProposal p, string outcome, string reason, CancellationToken ct) =>
        _audit.RecordAsync(
            new HolocronAudit
            {
                JobId = _jobId,
                PageId = _pageId,
                Kind = "fillgap",
                FromId = p.FromId,
                ToId = p.ToId,
                Label = p.Label,
                Claim = p.Claim ?? string.Empty,
                Reasoning = p.Reasoning,
                Evidence = p.Evidence?.Select(BuildAuditEvidence).ToList() ?? [],
                FromYear = p.FromYear,
                ToYear = p.ToYear,
                Outcome = outcome,
                OutcomeReason = string.IsNullOrEmpty(reason) ? null : reason,
                AgentVersion = HolocronAgent.AgentVersion,
            },
            ct
        );

    Task<string> RecordAddAuditAsync(HolocronAddEdgeProposal p, string outcome, string reason, CancellationToken ct) =>
        _audit.RecordAsync(
            new HolocronAudit
            {
                JobId = _jobId,
                PageId = _pageId,
                Kind = "add",
                FromId = p.FromId,
                ToId = p.ToId,
                Label = p.Label,
                Claim = p.Claim ?? string.Empty,
                Reasoning = p.Reasoning,
                Evidence = p.Evidence?.Select(BuildAuditEvidence).ToList() ?? [],
                FromYear = p.FromYear,
                ToYear = p.ToYear,
                Outcome = outcome,
                OutcomeReason = string.IsNullOrEmpty(reason) ? null : reason,
                AgentVersion = HolocronAgent.AgentVersion,
            },
            ct
        );

    Task<string> RecordPropertyAuditAsync(HolocronNodeProposalPayload p, string outcome, string reason, CancellationToken ct) =>
        _audit.RecordAsync(
            new HolocronAudit
            {
                JobId = _jobId,
                PageId = _pageId,
                Kind = "property",
                FieldPath = p.FieldPath,
                Values = p.Values?.ToList(),
                Claim = p.Claim ?? string.Empty,
                Reasoning = p.Reasoning,
                Evidence = p.Evidence?.Select(BuildAuditEvidence).ToList() ?? [],
                Outcome = outcome,
                OutcomeReason = string.IsNullOrEmpty(reason) ? null : reason,
                AgentVersion = HolocronAgent.AgentVersion,
            },
            ct
        );

    static EnrichmentEvidence BuildAuditEvidence(HolocronEvidencePayload e) =>
        new()
        {
            SourcePageId = e.SourcePageId ?? 0,
            ChunkId = e.ChunkId,
            Excerpt = e.Excerpt ?? string.Empty,
            RelevanceScore = e.RelevanceScore,
        };
}
