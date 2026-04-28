using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;

namespace StarWarsData.Services.AI.Agents;

/// <summary>
/// Phase 2 background agent. Reads <c>kg.nodes</c>, gathers context from related
/// nodes / edges / article chunks, and writes evidence-backed enrichments to
/// <c>kg.enrichments</c> / <c>kg.edge_enrichments</c>. Every state transition is
/// recorded in <c>kg.events</c> as an immutable audit log, which the Stage D
/// changelog UI will paginate over.
///
/// **v1 policy** (see <c>eng/design/018-kg-enrichments-architecture.md</c>) —
/// the wiki infobox is the canonical truth foundation. Holocron polishes around
/// the edges; it adds, it never contradicts:
///
/// <list type="bullet">
///   <item><b>Permitted operations</b>: <see cref="EnrichmentOperation.Add"/> (new field / new edge),
///         <see cref="EnrichmentOperation.Augment"/> (append to a list, deduped against existing values),
///         <see cref="EnrichmentOperation.FillGap"/> (fill a null sub-property like <c>edge.fromYear</c>).</item>
///   <item><b>Forbidden</b>: changing an existing non-null value (no Refine — the enum doesn't even include it).</item>
///   <item><b>Forbidden</b>: writing to <c>kg.nodes</c> / <c>kg.edges</c> — those belong to Phase 1.</item>
///   <item><b>Forbidden</b>: mutating identity fields (<c>pageId</c> / <c>name</c> / <c>type</c> / <c>wikiUrl</c>).</item>
/// </list>
///
/// **Pre-flight checks** (load-bearing, enforced in C# before any insert):
/// <list type="bullet">
///   <item><see cref="EnrichmentOperation.Add"/> property — <c>kg.nodes[pageId].properties[fieldPath]</c> must be missing or empty.</item>
///   <item><see cref="EnrichmentOperation.Add"/> edge — <c>(fromId, toId, label)</c> must NOT exist in <c>kg.edges</c> AND must NOT be Active in <c>kg.edge_enrichments</c>.</item>
///   <item><see cref="EnrichmentOperation.Augment"/> — each proposed list item must not already appear in the existing list (case-insensitive).</item>
///   <item><see cref="EnrichmentOperation.FillGap"/> — the targeted sub-property must currently be <c>null</c> in the base collection.</item>
///   <item>All operations — at least one <see cref="EnrichmentEvidence"/> with a real <c>sourcePageId</c> or <c>chunkId</c>.</item>
///   <item>All operations — stamp <c>contentHashAtCreation</c> from the source node so the post-Phase-1 staleness
///         sweep can flip mismatches to <see cref="EnrichmentStatus.Stale"/> instead of letting them go silent.</item>
/// </list>
/// </summary>
public sealed class HolocronAgent
{
    /// <summary>Bumped on every meaningful change to the enhancement prompt or schema. Stamped onto every enrichment + event.</summary>
    public const string AgentVersion = "holocron-v1.3.0";

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoCollection<ArticleChunk> _chunks;
    readonly IMongoCollection<NodeEnrichment> _enrichments;
    readonly IMongoCollection<EdgeEnrichment> _edgeEnrichments;
    readonly IMongoCollection<HolocronEvent> _events;
    readonly IMongoCollection<RelationshipLabel> _labels;
    readonly IChatClient _chatClient;
    readonly SemanticSearchService? _semanticSearch;
    readonly ILogger<HolocronAgent> _logger;
    readonly SettingsOptions _settings;

    /// <summary>
    /// Canonical edge-label vocabulary built once at construction from
    /// <see cref="InfoboxDefinitionRegistry.AllLabelDefinitions"/>. Pre-flight rejects
    /// any Add-edge proposal whose label isn't in this set, so the agent can't invent
    /// new synonyms (e.g. <c>employs</c> vs <c>hires</c> vs <c>employer_of</c>). The
    /// registry is derived from <c>FieldSemantics</c> — the curated mapping from
    /// infobox field names to canonical edge labels.
    /// </summary>
    readonly HashSet<string> _knownLabels;

    public HolocronAgent(IMongoClient mongoClient, IOptions<SettingsOptions> settings, IChatClient chatClient, ILogger<HolocronAgent> logger, SemanticSearchService? semanticSearch = null)
    {
        _settings = settings.Value;
        var db = mongoClient.GetDatabase(_settings.DatabaseName);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _chunks = db.GetCollection<ArticleChunk>(Collections.SearchChunks);
        _enrichments = db.GetCollection<NodeEnrichment>(Collections.KgEnrichments);
        _edgeEnrichments = db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
        _events = db.GetCollection<HolocronEvent>(Collections.KgEvents);
        _labels = db.GetCollection<RelationshipLabel>(Collections.KgLabels);
        _chatClient = chatClient;
        _semanticSearch = semanticSearch;
        _logger = logger;

        _knownLabels = InfoboxDefinitionRegistry.AllLabelDefinitions().Select(d => d.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Daily orchestrator: emits <see cref="HolocronEventType.HolocronPassStarted"/>,
    /// runs the staleness sweep, picks N target nodes (configurable via
    /// <see cref="SettingsOptions.HolocronNodesPerPass"/>), enhances each sequentially,
    /// and emits <see cref="HolocronEventType.HolocronPassCompleted"/> with summary counts.
    /// Short-circuits when <see cref="SettingsOptions.HolocronEnabled"/> is false.
    /// </summary>
    public async Task RunDailyPassAsync(CancellationToken ct = default)
    {
        if (!_settings.HolocronEnabled)
        {
            _logger.LogInformation("HolocronAgent: HolocronEnabled=false — skipping daily pass.");
            return;
        }

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

        var targets = await PickNodesForEnhancementAsync(_settings.HolocronNodesPerPass, ct);
        var totalEnriched = 0;
        var totalEvidenceFailures = 0;

        foreach (var pageId in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var summary = await EnhanceNodeAsync(pageId, "scheduled", ct);
                totalEnriched += summary.EnrichmentsCreated;
                totalEvidenceFailures += summary.EvidenceFailures;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HolocronAgent: enhancement failed for PageId={PageId}; continuing pass.", pageId);
            }
        }

        await EmitEventAsync(
            new HolocronEvent
            {
                EventType = HolocronEventType.HolocronPassCompleted,
                Summary =
                    $"Holocron daily pass complete. Targets: {targets.Count}, "
                    + $"enrichments: {totalEnriched}, evidence failures: {totalEvidenceFailures}, "
                    + $"stale flagged: {stalenessSummary.MarkedStale}, orphaned: {stalenessSummary.MarkedOrphaned}.",
                TriggeredBy = "scheduled",
                AgentVersion = AgentVersion,
            },
            ct
        );

        _logger.LogInformation(
            "HolocronAgent: daily pass completed in {Duration}. Targets: {Targets}, enrichments: {Created}, evidence failures: {Failures}, stale: {Stale}, orphaned: {Orphaned}.",
            DateTime.UtcNow - started,
            targets.Count,
            totalEnriched,
            totalEvidenceFailures,
            stalenessSummary.MarkedStale,
            stalenessSummary.MarkedOrphaned
        );
    }

    /// <summary>
    /// Walk every <see cref="EnrichmentStatus.Active"/> node enrichment and compare its
    /// <see cref="NodeEnrichment.ContentHashAtCreation"/> against the current source
    /// node's <see cref="GraphNode.ContentHash"/>. Mismatches flip to
    /// <see cref="EnrichmentStatus.Stale"/>; targets pointing at deleted pages flip to
    /// <see cref="EnrichmentStatus.Stale"/> with reason <c>page_deleted</c>.
    ///
    /// Edge enrichments use a combined hash <c>fromContentHash|toContentHash</c> so that
    /// either endpoint changing invalidates the enrichment.
    ///
    /// Runs after every Phase 1 rebuild, before the daily enhancement pass.
    /// </summary>
    public async Task<StalenessSweepSummary> RunStalenessSweepAsync(CancellationToken ct = default)
    {
        // Snapshot active enrichments + current node hashes so we can compare without
        // doing a query per enrichment. The volumes are small enough (thousands, not
        // millions) that a single load is fine.
        var activeNodeEnrichments = await _enrichments.Find(e => e.Status == EnrichmentStatus.Active).ToListAsync(ct);
        var activeEdgeEnrichments = await _edgeEnrichments.Find(e => e.Status == EnrichmentStatus.Active).ToListAsync(ct);

        if (activeNodeEnrichments.Count == 0 && activeEdgeEnrichments.Count == 0)
        {
            _logger.LogInformation("HolocronAgent: staleness sweep — no active enrichments.");
            return new StalenessSweepSummary(MarkedStale: 0, MarkedOrphaned: 0, Total: 0);
        }

        var pageIds = activeNodeEnrichments.Select(e => e.PageId).Concat(activeEdgeEnrichments.SelectMany(e => new[] { e.FromId, e.ToId })).Distinct().ToList();
        var nodeHashes = await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, pageIds)).Project(n => new { n.PageId, n.ContentHash }).ToListAsync(ct);
        var hashByPageId = nodeHashes.ToDictionary(n => n.PageId, n => n.ContentHash);

        var stale = 0;
        var orphaned = 0;

        // Node enrichments — direct hash comparison
        var nodeUpdates = new List<WriteModel<NodeEnrichment>>();
        var nodeEvents = new List<HolocronEvent>();
        foreach (var en in activeNodeEnrichments)
        {
            if (!hashByPageId.TryGetValue(en.PageId, out var currentHash))
            {
                // Source node deleted — orphan
                nodeUpdates.Add(BuildStaleUpdate<NodeEnrichment>(en.Id));
                nodeEvents.Add(BuildStaleEvent(en, HolocronEventType.EnrichmentOrphaned, "page_deleted"));
                orphaned++;
            }
            else if (string.IsNullOrEmpty(currentHash) || string.IsNullOrEmpty(en.ContentHashAtCreation))
            {
                // Stub-node case (~0.05% of kg.nodes): Phase 1 couldn't compute a hash because
                // the raw page had no infobox.Data. We can't tell whether the source has changed,
                // so we deliberately don't flip these to Stale — better to surface a possibly-stale
                // enrichment than to discard ones we can't verify. When Phase 1 eventually downloads
                // the full page, this branch stops firing and the normal comparison takes over.
            }
            else if (!string.Equals(currentHash, en.ContentHashAtCreation, StringComparison.Ordinal))
            {
                nodeUpdates.Add(BuildStaleUpdate<NodeEnrichment>(en.Id));
                nodeEvents.Add(BuildStaleEvent(en, HolocronEventType.EnrichmentMarkedStale, "content_changed"));
                stale++;
            }
        }
        if (nodeUpdates.Count > 0)
            await _enrichments.BulkWriteAsync(nodeUpdates, cancellationToken: ct);

        // Edge enrichments — combined-hash comparison
        var edgeUpdates = new List<WriteModel<EdgeEnrichment>>();
        var edgeEvents = new List<HolocronEvent>();
        foreach (var en in activeEdgeEnrichments)
        {
            var fromHash = hashByPageId.GetValueOrDefault(en.FromId);
            var toHash = hashByPageId.GetValueOrDefault(en.ToId);

            if (fromHash is null || toHash is null)
            {
                edgeUpdates.Add(BuildStaleUpdate<EdgeEnrichment>(en.Id));
                edgeEvents.Add(BuildStaleEdgeEvent(en, HolocronEventType.EnrichmentOrphaned, "endpoint_deleted"));
                orphaned++;
            }
            else if (string.IsNullOrEmpty(fromHash) || string.IsNullOrEmpty(toHash) || string.IsNullOrEmpty(en.ContentHashAtCreation))
            {
                // Stub-endpoint case — at least one side has no Phase 1 hash. Same conservative
                // policy as node enrichments: don't flip to Stale, just skip the staleness check.
            }
            else
            {
                var combined = $"{fromHash}|{toHash}";
                if (!string.Equals(combined, en.ContentHashAtCreation, StringComparison.Ordinal))
                {
                    edgeUpdates.Add(BuildStaleUpdate<EdgeEnrichment>(en.Id));
                    edgeEvents.Add(BuildStaleEdgeEvent(en, HolocronEventType.EnrichmentMarkedStale, "content_changed"));
                    stale++;
                }
            }
        }
        if (edgeUpdates.Count > 0)
            await _edgeEnrichments.BulkWriteAsync(edgeUpdates, cancellationToken: ct);

        var allEvents = nodeEvents.Concat(edgeEvents).ToList();
        if (allEvents.Count > 0)
            await _events.InsertManyAsync(allEvents, cancellationToken: ct);

        var total = activeNodeEnrichments.Count + activeEdgeEnrichments.Count;
        _logger.LogInformation("HolocronAgent: staleness sweep — {Total} active enrichments, {Stale} flagged stale, {Orphaned} orphaned.", total, stale, orphaned);
        return new StalenessSweepSummary(MarkedStale: stale, MarkedOrphaned: orphaned, Total: total);
    }

    /// <summary>
    /// Enhance one node: gather context, ask the LLM for evidence-backed enrichment proposals,
    /// validate evidence + run pre-flight checks, supersede prior active enrichments for matching
    /// targets, and append the new ones plus <see cref="HolocronEventType.EnrichmentCreated"/> /
    /// <see cref="HolocronEventType.EdgeEnrichmentCreated"/> events.
    /// </summary>
    public async Task<NodeEnhancementSummary> EnhanceNodeAsync(int pageId, string triggeredBy = "manual", CancellationToken ct = default)
    {
        var context = await BuildContextAsync(pageId, ct);
        if (context is null)
        {
            _logger.LogWarning("HolocronAgent: EnhanceNodeAsync — PageId={PageId} not found in kg.nodes.", pageId);
            return new NodeEnhancementSummary(pageId, 0, 0);
        }

        if (string.IsNullOrEmpty(context.Node.ContentHash))
        {
            _logger.LogWarning("HolocronAgent: EnhanceNodeAsync — PageId={PageId} has no contentHash; skipping (Phase 1 hasn't stamped it).", pageId);
            return new NodeEnhancementSummary(pageId, 0, 0);
        }

        var batch = await CallLlmForProposalsAsync(context, ct);
        if (batch is null)
        {
            return new NodeEnhancementSummary(pageId, 0, 0);
        }

        var (created, evidenceFailures) = await ApplyProposalsAsync(context, batch, triggeredBy, ct);

        _logger.LogInformation(
            "HolocronAgent: EnhanceNodeAsync — PageId={PageId} ({Name}) — proposed nodes={NodeProp} annotate={Annotate} fillGap={FillGap} add={Add}, applied {Applied}, evidence failures {EvFails}.",
            pageId,
            context.Node.Name,
            batch.NodeProposals.Count,
            batch.AnnotateEdges.Count,
            batch.FillGapEdges.Count,
            batch.AddEdges.Count,
            created,
            evidenceFailures
        );

        return new NodeEnhancementSummary(pageId, created, evidenceFailures);
    }

    // ── Target selection ──────────────────────────────────────────────────

    /// <summary>
    /// Pick nodes that haven't been enhanced yet (no Active enrichments). Skips qualifier
    /// node types (TitleOrPosition, ForcePower, LightsaberForm, Era, Year) and nodes
    /// without a contentHash. Random sample order to spread coverage across runs.
    /// </summary>
    async Task<List<int>> PickNodesForEnhancementAsync(int count, CancellationToken ct)
    {
        if (count <= 0)
            return [];

        // Pull the set of pageIds with active enrichments so we can exclude them.
        var withEnrichments = await _enrichments
            .Distinct(new ExpressionFieldDefinition<NodeEnrichment, int>(e => e.PageId), Builders<NodeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active))
            .ToListAsync(ct);

        var skipTypes = new[] { KgNodeTypes.TitleOrPosition, KgNodeTypes.ForcePower, KgNodeTypes.LightsaberForm, KgNodeTypes.Era, KgNodeTypes.Year, KgNodeTypes.Unknown };

        var filter = Builders<GraphNode>.Filter.Nin(n => n.Type, skipTypes) & Builders<GraphNode>.Filter.Ne(n => n.ContentHash, null) & Builders<GraphNode>.Filter.Nin(n => n.PageId, withEnrichments);

        // $sample for randomised selection without loading the full collection.
        var sampled = await _nodes.Aggregate().Match(filter).Sample(count).Project(n => n.PageId).ToListAsync(ct);

        return sampled;
    }

    // ── Context gathering ─────────────────────────────────────────────────

    /// <summary>
    /// Pull the target node, 1-hop neighbours, and article chunks from three sources:
    /// (1) the target's own page, (2) pages that link to the target with passages mentioning
    /// it by name, and (3) vector-similar chunks from across the corpus. Returns null if
    /// the node doesn't exist.
    ///
    /// The three-source split is the difference between "the agent confirms what the wiki
    /// already says about this node" (own-page only) and "the agent surfaces what *other*
    /// pages say *about* this node" (linking + vector). Without (2) and (3), the agent
    /// has no access to cross-references the wiki has but the infobox didn't capture.
    /// See Design-018 "Cross-page context gathering".
    /// </summary>
    async Task<HolocronContext?> BuildContextAsync(int pageId, CancellationToken ct)
    {
        var node = await _nodes.Find(n => n.PageId == pageId).FirstOrDefaultAsync(ct);
        if (node is null)
            return null;

        // 1-hop neighbours via outgoing + incoming edges. Limit + relabel inbound to forward.
        var maxNeighbours = Math.Max(1, _settings.HolocronMaxNeighborsForContext);
        var halfLimit = Math.Max(1, maxNeighbours / 2);

        var outEdges = await _edges.Find(Builders<RelationshipEdge>.Filter.Eq(e => e.FromId, pageId)).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);
        var inEdges = await _edges.Find(Builders<RelationshipEdge>.Filter.Eq(e => e.ToId, pageId)).SortByDescending(e => e.Weight).Limit(halfLimit).ToListAsync(ct);

        var neighbourIds = outEdges.Select(e => e.ToId).Concat(inEdges.Select(e => e.FromId)).Distinct().ToList();
        var neighbourNodes =
            neighbourIds.Count == 0
                ? []
                : await _nodes
                    .Find(Builders<GraphNode>.Filter.In(n => n.PageId, neighbourIds))
                    .Project(n => new HolocronNeighbourSummary(n.PageId, n.Name, n.Type, n.StartYear, n.EndYear))
                    .ToListAsync(ct);

        var allChunks = new List<HolocronChunkSummary>();

        // Source 1 — own-page chunks (highest authority for what the wiki asserts about
        // the target itself; already part of the canonical infobox-derived view).
        var ownLimit = Math.Max(0, _settings.HolocronOwnPageChunks);
        if (ownLimit > 0)
        {
            var ownChunks = await _chunks
                .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, pageId))
                .SortBy(c => c.ChunkIndex)
                .Limit(ownLimit)
                .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.OwnPage, c.Links))
                .ToListAsync(ct);
            allChunks.AddRange(ownChunks);
        }

        // Source 2 — wiki-backlink chunks. The chunk corpus stores the raw HTML for
        // each article, so the target's `wikiUrl` appears verbatim as
        // `<a href="...">` wherever another article wikilinks to this entity.
        // That's the strongest "this article is talking about that page" signal —
        // exact, prose-aware, no infobox-edge prerequisite.
        //
        // Querying the URL with a regex over `Text` would scan all 800K+ chunks
        // (no index supports arbitrary substring regex), so we use the indexed
        // `Links` multikey field populated by ArticleChunkingService and the
        // 0012-extract-chunk-links migration. `Links` is the deduped set of
        // `<a href>` URLs extracted at chunk-write time; an equality lookup on
        // the multikey index is O(log N).
        var linkingLimit = Math.Max(0, _settings.HolocronLinkingPageChunks);
        if (linkingLimit > 0 && !string.IsNullOrWhiteSpace(node.WikiUrl))
        {
            var backlinkFilter = Builders<ArticleChunk>.Filter.AnyEq(c => c.Links, node.WikiUrl) & Builders<ArticleChunk>.Filter.Ne(c => c.PageId, pageId);
            const int perPageLimit = 2;

            var backlinkChunks = await _chunks
                .Find(backlinkFilter)
                .SortBy(c => c.PageId)
                .ThenBy(c => c.ChunkIndex)
                .Limit(linkingLimit * 3) // over-fetch so the per-page cap spreads across multiple articles
                .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.LinkingPage, c.Links))
                .ToListAsync(ct);

            var groupedByPage = backlinkChunks.GroupBy(c => c.PageId).SelectMany(g => g.Take(perPageLimit)).Take(linkingLimit).ToList();
            allChunks.AddRange(groupedByPage);
        }

        // Source 3 — vector-similar chunks. SemanticSearchService is registered in API but
        // optional in Admin (the Hangfire job path). When unavailable, just skip — we still
        // have own + linking coverage.
        var vectorLimit = Math.Max(0, _settings.HolocronVectorChunks);
        if (vectorLimit > 0 && _semanticSearch is not null && !string.IsNullOrWhiteSpace(node.Name))
        {
            // Compose a focused query — name + type biases the vector search toward chunks
            // discussing the target itself rather than the type's category broadly.
            var query = $"{node.Name} ({node.Type})";
            try
            {
                // Over-fetch a bit then filter out the target's own page (which is already in
                // source 1) and any pages we already covered in source 2.
                var coveredPageIds = allChunks.Select(c => c.PageId).ToHashSet();
                coveredPageIds.Add(pageId);

                var hits = await _semanticSearch.SearchAsync(query, types: null, continuity: null, realm: null, limit: vectorLimit * 3, minScore: 0.0);
                // SemanticSearchHit doesn't surface Links — re-fetch the chunks by id so
                // the prompt can render their wiki-linked entities alongside text.
                var hitChunkIds = hits.Where(h => !coveredPageIds.Contains(h.PageId) && !string.IsNullOrEmpty(h.ChunkId)).Take(vectorLimit).Select(h => h.ChunkId).ToList();
                var vectorChunks =
                    hitChunkIds.Count == 0
                        ? new List<HolocronChunkSummary>()
                        : await _chunks
                            .Find(Builders<ArticleChunk>.Filter.In(c => c.Id, hitChunkIds))
                            .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.VectorSimilar, c.Links))
                            .ToListAsync(ct);
                allChunks.AddRange(vectorChunks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HolocronAgent: vector search failed for PageId={PageId} ({Name}); continuing with own-page + linking-page chunks only.", pageId, node.Name);
            }
        }

        // Canonical edge-label vocabulary scoped to this node's type. Top-K by usage so
        // the agent sees what other nodes of the same type actually use, biasing it
        // toward consistent labels rather than synonyms. Fall back to top labels overall
        // if this type is too narrow (e.g. a rare type with no observed labels yet).
        var labelsForType = await _labels.Find(Builders<RelationshipLabel>.Filter.AnyEq(l => l.FromTypes, node.Type)).SortByDescending(l => l.UsageCount).Limit(25).ToListAsync(ct);
        if (labelsForType.Count < 5)
        {
            // Augment with overall top labels so the agent always has a working vocabulary.
            var overall = await _labels.Find(Builders<RelationshipLabel>.Filter.Empty).SortByDescending(l => l.UsageCount).Limit(25 - labelsForType.Count).ToListAsync(ct);
            var seen = labelsForType.Select(l => l.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            labelsForType.AddRange(overall.Where(l => !seen.Contains(l.Label)));
        }
        var canonicalLabels = labelsForType.Select(l => new HolocronLabelSummary(l.Label, l.Reverse, l.Description, l.UsageCount)).ToList();

        return new HolocronContext(node, outEdges, inEdges, neighbourNodes, allChunks, canonicalLabels);
    }

    // ── Async pipeline integration (Design-020) ───────────────────────────

    /// <summary>
    /// Run one extractor batch from the async workflow pipeline. Builds a
    /// <see cref="HolocronContext"/> from the snapshot DTOs supplied by the
    /// pipeline (so the workflow doesn't need to import private records) and
    /// calls the same prompt + structured-output path the synchronous
    /// <see cref="EnhanceNodeAsync"/> uses. Returns the raw
    /// <see cref="HolocronProposalsBatch"/> — apply / pre-flight is the
    /// pipeline's responsibility (see HolocronConsolidatorExecutor +
    /// HolocronApplyExecutor).
    /// </summary>
    public Task<HolocronProposalsBatch?> CallLlmForBatchAsync(
        Holocron.HolocronNodeSnapshot node,
        List<RelationshipEdge> outEdges,
        List<RelationshipEdge> inEdges,
        IReadOnlyList<Holocron.HolocronNeighbour> neighbours,
        IReadOnlyList<Holocron.HolocronCanonicalLabel> canonicalLabels,
        IReadOnlyList<Holocron.HolocronChunkPayload> ownPageChunks,
        IReadOnlyList<Holocron.HolocronChunkPayload> batchChunks,
        CancellationToken ct = default
    )
    {
        // Build a transient GraphNode the prompt builder can consume. Identity +
        // properties + temporal facets — that's all BuildUserPrompt actually reads.
        var graphNode = new GraphNode
        {
            PageId = node.PageId,
            Name = node.Name,
            Type = node.Type,
            ContentHash = node.ContentHash,
            WikiUrl = node.WikiUrl,
            Continuity = node.Continuity,
            Realm = node.Realm,
            StartYear = node.StartYear,
            EndYear = node.EndYear,
            Properties = node.Properties,
            TemporalFacets = node
                .TemporalFacets.Select(f => new TemporalFacet
                {
                    Semantic = f.Semantic,
                    Calendar = f.Calendar,
                    Year = f.Year,
                    Text = f.Text,
                })
                .ToList(),
        };

        var neighbourSummaries = neighbours.Select(n => new HolocronNeighbourSummary(n.PageId, n.Name, n.Type, n.StartYear, n.EndYear)).ToList();
        var labelSummaries = canonicalLabels.Select(l => new HolocronLabelSummary(l.Label, l.Reverse, l.Description, l.UsageCount)).ToList();
        var chunks = ownPageChunks
            .Select(c => new HolocronChunkSummary(c.ChunkId, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.OwnPage, c.Links))
            .Concat(batchChunks.Select(c => new HolocronChunkSummary(c.ChunkId, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.LinkingPage, c.Links)))
            .ToList();

        var context = new HolocronContext(graphNode, outEdges, inEdges, neighbourSummaries, chunks, labelSummaries);
        return CallLlmForProposalsAsync(context, ct);
    }

    // ── LLM call ──────────────────────────────────────────────────────────

    async Task<HolocronProposalsBatch?> CallLlmForProposalsAsync(HolocronContext context, CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt();
        // Resolve every wiki URL referenced by this batch's chunks to (PageId, Name, Type)
        // via a single bulk lookup against kg.nodes. The resolved entities are surfaced
        // inline per-chunk in the prompt so the agent can emit `addEdges` with concrete
        // target PageIds (e.g. when a chunk says "she worked as a [[bounty hunter]]",
        // the agent sees that "bounty hunter" resolves to PageId 456298 / TitleOrPosition
        // and can propose `has_role` with that target — no need to invent property names).
        var linkedEntities = await ResolveLinkedEntitiesAsync(context.Chunks, ct);
        var userPrompt = BuildUserPrompt(context, linkedEntities);

        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt), new(ChatRole.User, userPrompt) };

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<HolocronProposalsBatch>(schemaName: "holocron_proposals", schemaDescription: "Evidence-backed enrichment proposals for one KG node"),
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, ct);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("HolocronAgent: empty LLM response for PageId={PageId}.", context.Node.PageId);
                return null;
            }
            var batch = JsonSerializer.Deserialize<HolocronProposalsBatch>(text, JsonOpts);
            if (batch is null)
            {
                _logger.LogWarning("HolocronAgent: LLM returned null/empty proposals batch for PageId={PageId}.", context.Node.PageId);
                return null;
            }
            return batch;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "HolocronAgent: failed to parse structured LLM response for PageId={PageId}.", context.Node.PageId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HolocronAgent: LLM call failed for PageId={PageId}.", context.Node.PageId);
            return null;
        }
    }

    static string BuildSystemPrompt() =>
        """
            You are the Holocron — a careful curator of a Star Wars knowledge graph.

            # Directive

            ENHANCE the target node in the user prompt using evidence from the article
            chunks, neighbour nodes, and existing edges supplied. The wiki infobox is the
            canonical truth foundation — Phase 1 already extracted it. You ADD, you never
            CONTRADICT.

            Each run produces ONE structured response covering FOUR enhancement vectors.
            Examine all four every run. The four arrays are not ranked — they solve
            different problems and any combination may apply. Returning empty arrays for
            vectors where the evidence doesn't support enrichment is the correct answer
            when nothing is missing. Quality over quantity.

            The output schema is enforced by the structured-output JSON Schema — you do not
            need to describe field shapes; the runtime constrains them. Your job is to
            decide which entries belong in which array, and what their content should be.

            # The four vectors

            ## annotateEdges — context for an existing edge
            For each item in the user prompt's "Edges available for ANNOTATE" list, decide
            whether the chunks reveal role / qualifier / description context worth
            attaching. Pick (fromId, toId, label) verbatim from that list. Pack role +
            qualifier + description for one edge into ONE item — never two annotateEdges
            items for the same (fromId, toId, label).

            ## fillGapEdges — temporal bounds for an existing edge
            For each item in the user prompt's "FillGap candidates" list, supply fromYear
            and/or toYear when chunks let you cite a year. Bounds in that list are tagged:
              - null — fill with a chunk-cited year if you find one.
              - [lifecycle, refinable] — derived from endpoint lifespans, often too broad.
                You may overwrite with a tighter, chunk-cited year. Example: an
                `apprentice_of` edge with fromYear=-41 [lifecycle, refinable] is asserting
                the apprenticeship started at the apprentice's birth — almost certainly
                wrong; refine it.
              - [infobox, hard] — wiki-stated directly. Do NOT touch. Use annotateEdges
                if you have new context.
              - [unknown, hard] — provenance unclear; treat as hard.

            ## nodeProposals — new property values on the target node
            Append a value or fill a missing property on the target node. The server
            decides Add (no existing values) vs Augment (extend the list) — you just say
            "this field should contain these values" with evidence.

            See **Edges vs Properties** below for when to use this vector vs the edge
            vectors. Most "I want to add information" instincts are actually edges.

            **Hard rules for `fieldPath`:**

            - `fieldPath` MUST appear verbatim in the user prompt's "Canonical property
              fieldPaths" list. Use the exact casing shown. The consolidator rejects any
              fieldPath that doesn't match — including case variants like
              `affiliation` / `Affiliation` / `Affiliations`, invented names like
              `has role` / `member of family` / `affiliated with`, AND fieldPaths that
              are valid for OTHER templates but not this one (e.g. `Primary role(s)`
              is a Starship free-text field, not a Character property — proposing it
              on a Character page gets rejected).

            ## addEdges — a brand-new edge to a DIFFERENT node
            Use this when chunk text evidences a relationship to ANOTHER entity that
            doesn't already have an edge to the target.

            **How to find target PageIds:** every chunk in the user prompt is followed by
            a "Wiki entities linked in this chunk's text" section listing the PageId,
            Name, and Type of each wiki entity referenced inline. When chunk text says
            "she worked as a [[bounty hunter]]", that "[[bounty hunter]]" link resolves
            to (e.g.) PageId 456298 / Type=TitleOrPosition. Use that PageId as the
            `toId` of an `addEdges` proposal with `label: "has_role"`. Same pattern for
            `member_of → Religion node`, `serves_in → Military_unit node`, etc.

            **This is also the answer to "but the target node didn't show up in
            Annotate/FillGap candidates":** the existing-edges candidate lists only
            cover edges that ALREADY exist. New edges to entities the chunk text
            references (and that resolve via the inline link section) belong on
            `addEdges`. Don't fall back to `nodeProposals` for relationships — properties
            are flat strings, edges are how relationships are encoded.

            **Hard rules:**

            - The (fromId, toId) pair must NOT already appear in Annotate or FillGap
              candidate lists in EITHER direction with ANY label. If it does, use
              annotateEdges instead — two parallel edges between the same pair are
              forbidden.
            - The `label` MUST come from the user prompt's "Canonical edge labels"
              vocabulary. Do NOT invent synonyms (`member_of` next to an existing
              `affiliated_with`). If no canonical label fits, emit nothing for that
              edge.
            - The `toId` MUST be a real PageId — either from the chunks' "Wiki entities
              linked in this chunk's text" sections, or from a neighbour you found in
              the existing-edges lists. Don't hallucinate PageIds.

            # Edges vs Properties — when to use which

            The graph is fundamentally **entities (nodes)** and the **relationships
            between them (edges)**. Properties are flat scalar attributes of a single
            node. Most "I want to add information about X" instincts are actually
            edges, not properties.

            **Use an edge when the value:**

              - is another meaningful entity (a person, place, organisation, role,
                title, species, family, ship, battle, weapon, event…)
              - is shared by many nodes — many characters share "Bounty hunter",
                "Jedi Order", "Human", "Tatooine"
              - is useful for traversal / pathfinding — "who else holds this role?
                who else is in this faction?"
              - has its own attributes — the role itself has a description, the
                faction has its own members, the ship has its own specs
              - can change over time — someone gains or loses a title, joins or
                leaves a faction
              - is something you want to reason over semantically — "did Anakin
                and Asajj serve the same master?" only works if Sidious and Dooku
                are nodes, not strings

            **Use a property when the value:**

              - is a flat scalar of the entity itself (height, eye colour, gender,
                hair colour, classification, designation, mass)
              - has no independent existence — "blue eyes" is not an entity; "180 cm"
                is not an entity; "Force-sensitive" is an attribute, not an entity
              - is a measurement, descriptor, or enum-like label that's stable for
                the node and has no meaningful temporal semantics

            **Strong test:** if you can imagine a wiki page existing for this value,
            it's a node — emit an edge to it. "Bounty hunter" has a wiki page.
            "Sith" has a wiki page. "Nightsisters" has a wiki page. "Blue eyes"
            does not. "Force-sensitive" does not.

            **The "Wiki entities linked in this chunk's text" section is your map.**
            Every chunk lists the wiki entities referenced inline. When a chunk
            says "Asajj worked as a [[bounty hunter]]" and the linked-entities
            section resolves `[[bounty hunter]]` to
            `(PageId 456298, Type=TitleOrPosition)`, that is a direct signal:
            this is a node, emit `has_role → 456298`. The same goes for
            `[[Confederacy of Independent Systems]]` → `member_of`/`affiliated_with`,
            `[[Dathomir]]` → `homeworld`, `[[Dooku]]` → `apprentice_of`. Linked
            entities are nodes by construction — they exist in the KG already.

            # No double-encoding — one fact, one place

            A single fact is encoded ONCE in its strongest form. The consolidator
            cross-checks edges and properties in the same run and DROPS property
            values that overlap with an edge target's name.

              - If you propose `addEdges: has_role → Bounty hunter`, do NOT also
                write "bounty hunter" / "Bounty hunter" into `Titles`, `Aliases`,
                or any other property. The edge is the canonical store; the
                duplicate property value gets dropped.
              - If you propose `addEdges: member_of → Nightsisters`, do NOT also
                write "Nightsister" into `Aliases` or any affiliation-flavoured
                property.
              - If two property fieldPaths cover the same concept (e.g. `Occupation`
                and `Primary role(s)`), pick the ONE canonical for this node's
                template — never write both. Most templates only allow one of them.
              - **Aliases are alternate proper-noun NAMES, never role/title/faction
                strings.** "Darth Tyranus" is an alias for Dooku. "Old Ben" is an
                alias for Obi-Wan. "Bounty hunter", "Sith apprentice", "Nightsister",
                "Black Sun", "Dark acolyte" are NOT aliases — they are roles,
                affiliations, or species, encoded as edges. The consolidator
                rejects any Aliases value that resolves to a TitleOrPosition,
                Government, Organization, Religion, Species, Family, MilitaryUnit,
                or CulturalGroup node.
              - Descriptive epithets coined by combining a role with the
                character's role-context ("The Bounty Hunter", "The Pale Witch",
                "Queen of the Nightsisters") are NOT aliases either. Aliases must
                be names that wikis and characters actually use to refer to the
                person interchangeably.

            # Evidence rules (every proposal)

            - Every proposal MUST cite at least one chunk (chunkId + verbatim excerpt) or
              one neighbour node (sourcePageId).
            - Cite only what is in the user prompt. Do not invent sources.
            - Direct evidence (chunk states the fact) is the gold standard. Indirect
              evidence (chunk implies the fact) is acceptable when you explain the
              inference in the reasoning field. Example: a chunk that says "as Darth
              Vader, Anakin commanded Sidious's forces from 19 BBY" implies the
              apprentice_of relationship was active by 19 BBY, even though it doesn't use
              the word "apprentice". Do not infer beyond what the chunk supports.
            - Conflicting evidence: prefer (1) the target's own page over a backlink,
              (2) a chunk citing a specific year over a vague era, (3) Canon over Legends.
              If you cannot reconcile, skip — emit nothing.
            - Specificity ladder: prefer the more specific claim when both are evidenced.
              "Jedi High Council" beats "Jedi Order"; "Jedi General" beats "Jedi".
            - A chunk that mentions the entity in passing without adding new context is
              not evidence — it's filler. Skip.
            - When unsure, emit nothing. Empty arrays are correct.

            # Continuity firewall

            The target node carries a Continuity flag (Canon or Legends). Treat it as a
            hard firewall — enrich only from chunks whose source page matches the target's
            continuity. Backlink chunks are not pre-filtered, so YOU must skip any chunk
            whose source page is from the opposite continuity.

            Legends signals: Expanded Universe, the New Jedi Order book series, characters
            that appeared only pre-2014 (Mara Jade, Galen Marek, Jacen Solo), Legends-
            tagged article titles. Canon signals: post-2014 publications, The Mandalorian,
            Ahsoka, Rebels, sequel-trilogy events, High Republic media. When ambiguous,
            skip.

            # Hard constraints (pre-flight rejects violations)

            - Never propose a value that contradicts the infobox.
            - Never touch a node property that already has a non-empty value.
            - annotateEdges / fillGapEdges: (fromId, toId, label) MUST appear verbatim in
              the corresponding candidate list.
            - addEdges: pair MUST NOT appear in EITHER candidate list, in EITHER
              direction. Label MUST be in the canonical vocabulary.
            - Every proposal MUST cite at least one chunk or neighbour.

            # Era reference (BBY = Before the Battle of Yavin, ABY = After)

            For FillGap when chunks cite an era rather than a year:
              Old Republic Era → −1000+ BBY (rare; usually skip)
              High Republic Era → ~−500 to −100 BBY
              Fall of the Jedi / Prequels → ~−32 to −19 BBY
              Clone Wars → −22 to −19 BBY
              Reign of the Empire / Imperial Era → −19 to 0 BBY
              Age of Rebellion / Galactic Civil War → 0 to 4 ABY
              New Republic Era → 4 to ~28 ABY
              Sequel Trilogy / Rise of the First Order → ~28 to 35 ABY

            Specific anchors: Battle of Naboo = 32 BBY, Geonosis = 22 BBY, Order 66 /
            Mustafar = 19 BBY, Yavin = 0 BBY, Hoth = 3 ABY, Endor = 4 ABY, Jakku = 5 ABY,
            Starkiller Base = 34 ABY, Exegol = 35 ABY.

            Prefer a specific anchor over an era range. "After Order 66" → fromYear = -19.
            "During the Clone Wars" with no other detail → emit only the bound you can
            pin (one side null is fine), never guess.

            # Worked examples

            annotateEdges — good. Existing edge: Anakin Skywalker —[married_to]→ Padmé.
            Chunk: "On Naboo in 22 BBY, Anakin secretly wed Padmé in defiance of the Jedi
            Code." Annotate with qualifier "secret marriage on Naboo, 22 BBY" and a
            description covering the secrecy. Reasoning cites the direct chunk evidence.

            annotateEdges — skip. Existing edge: Anakin Skywalker —[knew]→ Mace Windu.
            Chunk: "Anakin sat in the Council chamber, glancing across at Mace Windu."
            Skip — confirms only co-presence, which the existing edge already implies.
            No new role, qualifier, or description.

            fillGapEdges — good (direct). Candidate: Anakin —[apprentice_of]→ Sidious
            (fromYear=-41 [lifecycle, refinable]). Chunk: "On Mustafar in 19 BBY,
            Sidious dubbed his fallen disciple 'Darth Vader' — the Sith apprenticeship
            beginning that day." → fromYear = -19. The lifecycle bound -41 (Anakin's
            birth) is too broad; Mustafar is the canonical start.

            fillGapEdges — good (indirect). Candidate: Anakin —[led]→ 501st Legion
            (fromYear=-41 [lifecycle, refinable]). Chunk: "At Christophsis (22 BBY),
            General Skywalker led the 501st in their first major engagement of the Clone
            Wars." → fromYear = -22 as a lower bound; reasoning explains the inference.

            addEdges — only when truly absent. Pair has no entry in either candidate list,
            chunk explicitly establishes a new relationship using a canonical label. Most
            runs produce zero addEdges.
            """;

    /// <summary>
    /// One wiki entity surfaced inline alongside a chunk's text so the agent can target
    /// it directly with <c>addEdges</c>. PageId and Type drive what edge label fits
    /// (e.g. <c>has_role</c> for TitleOrPosition targets, <c>member_of</c> for Religion).
    /// </summary>
    sealed record LinkedEntity(int PageId, string Name, string Type);

    /// <summary>
    /// Bulk-resolve every wiki URL across <paramref name="chunks"/>'s <c>Links</c>
    /// arrays to the matching <c>kg.nodes</c> row in a single round-trip. Returns a
    /// case-insensitive URL → entity dictionary so per-chunk rendering can list the
    /// linked entities inline.
    /// </summary>
    async Task<IReadOnlyDictionary<string, LinkedEntity>> ResolveLinkedEntitiesAsync(IReadOnlyList<HolocronChunkSummary> chunks, CancellationToken ct)
    {
        if (chunks is null || chunks.Count == 0)
            return new Dictionary<string, LinkedEntity>();
        var allUrls = chunks.Where(c => c.Links is not null).SelectMany(c => c.Links).Where(u => !string.IsNullOrEmpty(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allUrls.Count == 0)
            return new Dictionary<string, LinkedEntity>();
        var nodes = await _nodes
            .Find(Builders<GraphNode>.Filter.In(n => n.WikiUrl, allUrls))
            .Project(n => new
            {
                n.PageId,
                n.Name,
                n.Type,
                n.WikiUrl,
            })
            .ToListAsync(ct);
        var dict = new Dictionary<string, LinkedEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            if (string.IsNullOrEmpty(n.WikiUrl))
                continue;
            dict[n.WikiUrl] = new LinkedEntity(n.PageId, n.Name, n.Type);
        }
        return dict;
    }

    string BuildUserPrompt(HolocronContext context, IReadOnlyDictionary<string, LinkedEntity>? linkedEntities = null)
    {
        var sb = new StringBuilder();
        var node = context.Node;

        sb.Append("# Target node\n\n");
        sb.AppendFormat("PageId: {0}\nName: {1}\nType: {2}\nContinuity: {3}\nRealm: {4}\n", node.PageId, node.Name, node.Type, node.Continuity, node.Realm);
        if (node.StartYear.HasValue)
            sb.AppendFormat("StartYear: {0}\n", node.StartYear);
        if (node.EndYear.HasValue)
            sb.AppendFormat("EndYear: {0}\n", node.EndYear);
        sb.Append('\n');

        sb.Append("## Existing properties (from infobox — do NOT contradict)\n\n");
        if (node.Properties.Count == 0)
        {
            sb.Append("(none populated)\n");
        }
        else
        {
            foreach (var (key, values) in node.Properties.OrderBy(p => p.Key))
            {
                sb.AppendFormat("- `{0}`: {1}\n", key, string.Join("; ", values.Take(8)));
            }
        }
        sb.Append('\n');

        // Canonical fieldPath inventory — same discipline as canonical edge labels.
        // The agent MUST pick fieldPath verbatim from this list; the consolidator
        // pre-flight rejects anything else (case-insensitive match). Suppresses
        // the case-/plural-variant duplicates and the "edge label as fieldPath"
        // mistake we saw in v1.1.0 (e.g. fieldPath: "has role" / "member of family").
        // Template-scoped property allowlist. ForTemplate(node.Type).Properties
        // is the intersection of the global FieldSemantics.Properties set with
        // THIS template's actual fields. Without this we leak Starship-only
        // fields like `Primary role(s)`, `Hull`, `Power plant`, `Sensor color`
        // into Character prompts, and the agent picks them. Phase E in the
        // consolidator does the matching template-scoped check.
        var templateProperties = InfoboxDefinitionRegistry.ForTemplate(node.Type).Properties;
        sb.AppendFormat("## Canonical property fieldPaths for template `{0}` (you MUST pick from this list — never invent or coin variants)\n\n", node.Type);
        sb.Append(
            "These are the only valid `fieldPath` values for `nodeProposals` on this template. Use the exact casing shown. Cross-template fields (e.g. Starship-only `Primary role(s)` on a Character) are pre-flight rejected.\n"
        );
        sb.Append("Most enrichments belong on the edge vectors, not here — see the Edges vs Properties section in your system instructions.\n\n");
        if (templateProperties.Count == 0)
        {
            sb.Append("(no scalar properties allowed for this template — emit only edges and temporal annotations)\n\n");
        }
        else
        {
            foreach (var fieldPath in templateProperties.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendFormat("- `{0}`\n", fieldPath);
            }
            sb.Append('\n');
        }

        sb.Append("## Existing temporal facets\n\n");
        if (node.TemporalFacets.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (var f in node.TemporalFacets.OrderBy(f => f.Semantic))
            {
                sb.AppendFormat("- {0} ({1}): year={2}, text=\"{3}\"\n", f.Semantic, f.Calendar, f.Year?.ToString() ?? "null", Truncate(f.Text, 80));
            }
        }
        sb.Append('\n');

        sb.Append("## Canonical edge labels (you MUST pick from this list — never invent or coin synonyms)\n\n");
        if (context.CanonicalLabels.Count == 0)
        {
            sb.Append("(label registry empty — do NOT propose any edges this run)\n");
        }
        else
        {
            sb.Append("Each line: `label` (reverse: `reverse_label`) [N uses] — description\n");
            foreach (var l in context.CanonicalLabels)
            {
                sb.AppendFormat(
                    "- `{0}` (reverse: `{1}`) [{2} uses] — {3}\n",
                    l.Label,
                    string.IsNullOrEmpty(l.Reverse) ? "—" : l.Reverse,
                    l.UsageCount,
                    string.IsNullOrEmpty(l.Description) ? "(no description)" : l.Description
                );
            }
            sb.AppendLine();
            sb.AppendLine("If your intended relationship is conceptually identical to one of these labels, USE that label.");
            sb.AppendLine("Examples of synonym pitfalls — `member_of` ≈ `affiliated_with`, `led` ≈ `commanded`, `employs` ≈ `hires` ≈ `employer_of`. Pick the canonical form.");
        }
        sb.Append('\n');

        // All edges touching this node — these are the Annotate candidates. Render once
        // with the full (fromId, toId, label) tuple so the agent can copy verbatim into
        // its proposal. The FillGap subset is rendered separately below for clarity, but
        // every edge here is also implicitly available for Annotate.
        var allEdges = context.OutgoingEdges.Concat(context.IncomingEdges).ToList();

        sb.Append("## Edges available for ANNOTATE (use these for the `annotateEdges` array)\n\n");
        sb.Append(
            "Pick `fromId`, `toId`, `label` verbatim from this list. Supply at least one of `role`, `qualifier`, `description` per item. Skip any edge where the chunks don't reveal new context.\n\n"
        );
        if (allEdges.Count == 0)
        {
            sb.Append("(no edges touch this node — Annotate has nothing to work with this run)\n");
        }
        else
        {
            foreach (var e in allEdges)
            {
                var fromName = e.FromId == node.PageId ? node.Name : e.FromName;
                var toName = e.ToId == node.PageId ? node.Name : e.ToName;
                sb.AppendFormat(
                    "- fromId={0} ({1}) —[{2}]→ toId={3} ({4})  (fromYear={5}, toYear={6}",
                    e.FromId,
                    fromName,
                    e.Label,
                    e.ToId,
                    toName,
                    e.FromYear?.ToString() ?? "null",
                    e.ToYear?.ToString() ?? "null"
                );
                if (e.Meta is not null && (!string.IsNullOrWhiteSpace(e.Meta.Qualifier) || !string.IsNullOrWhiteSpace(e.Meta.RawValue)))
                    sb.AppendFormat(", existing qualifier=\"{0}\"", Truncate(e.Meta.Qualifier ?? e.Meta.RawValue ?? string.Empty, 80));
                sb.Append(")\n");
            }
        }
        sb.Append('\n');

        // FillGap candidates: edges where AT LEAST ONE bound is refinable. A bound is
        // refinable when (a) it's null, or (b) it carries a Lifecycle / Unknown
        // BoundsSource (Phase 5 lifecycle-fallback derivation, soft upper bound — see
        // Design-021). Bounds tagged Infobox are hard and excluded.
        var fillGapCandidates = allEdges.Where(IsFillGapCandidate).ToList();
        sb.Append("## Edges with refinable temporal bounds — FillGap candidates (use these for the `fillGapEdges` array)\n\n");
        sb.Append(
            "Pick `fromId`, `toId`, `label` verbatim from this list. Fill / refine `fromYear` and/or `toYear` only when the chunks let you cite a specific year — never guess. Bounds marked `[infobox, hard]` are evidence-backed by the wiki and must NOT be touched; use Annotate for context instead.\n\n"
        );
        if (fillGapCandidates.Count == 0)
        {
            sb.Append("(every edge has hard infobox-supplied bounds — FillGap has nothing to do this run)\n");
        }
        else
        {
            foreach (var e in fillGapCandidates)
            {
                var fromName = e.FromId == node.PageId ? node.Name : e.FromName;
                var toName = e.ToId == node.PageId ? node.Name : e.ToName;
                sb.AppendFormat(
                    "- fromId={0} ({1}) —[{2}]→ toId={3} ({4})  (fromYear={5}, toYear={6}; {7})\n",
                    e.FromId,
                    fromName,
                    e.Label,
                    e.ToId,
                    toName,
                    FormatBoundForPrompt(e.FromYear, e.Meta?.BoundsSource),
                    FormatBoundForPrompt(e.ToYear, e.Meta?.BoundsSource),
                    DescribeFillScope(e)
                );
            }
        }
        sb.Append('\n');

        // Pairs already connected — explicit "do NOT Add to these" hint. Direction-agnostic.
        var connectedPairs = allEdges.Select(e => NodePairKey(e.FromId, e.ToId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("## Pairs already connected — do NOT propose Add edges between these pairs\n\n");
        if (connectedPairs.Count == 0)
        {
            sb.Append("(none — every Add candidate is structurally fair game)\n");
        }
        else
        {
            sb.AppendFormat(
                "These {0} unordered pair(s) already have an edge in the graph (in some direction, with some label). Use Annotate to enrich the existing edge; never add a parallel one.\n\n",
                connectedPairs.Count
            );
            foreach (var p in connectedPairs)
            {
                sb.AppendFormat("- {0}\n", p);
            }
        }
        sb.Append('\n');

        sb.Append("## Neighbour nodes (use these PageIds when citing or proposing edges)\n\n");
        if (context.Neighbours.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (var n in context.Neighbours)
            {
                sb.AppendFormat("- PageId={0}, Name=\"{1}\", Type={2}", n.PageId, n.Name, n.Type);
                if (n.StartYear.HasValue || n.EndYear.HasValue)
                    sb.AppendFormat(", years=[{0}..{1}]", n.StartYear?.ToString() ?? "?", n.EndYear?.ToString() ?? "?");
                sb.Append('\n');
            }
        }
        sb.Append('\n');

        sb.Append("## Article chunks (your primary evidence source — cite chunkId in evidence)\n\n");
        if (context.Chunks.Count == 0)
        {
            sb.Append("(no chunks available — proceed with neighbours only or emit nothing)\n");
        }
        else
        {
            // Group chunks by origin so the agent can weight evidence appropriately.
            // Authority hierarchy: target's own page > linking page > vector-similar.
            // Vector-similar chunks only appear on the synchronous EnhanceNodeAsync path
            // (semantic search supplements own + linking); the async pipeline does not
            // surface them yet but that may change, so the renderer handles all three.
            RenderChunkSection(sb, "Target's own page (highest authority for what the wiki asserts about this entity)", context.Chunks.Where(c => c.Origin == ChunkOrigin.OwnPage), linkedEntities);
            RenderChunkSection(
                sb,
                "Pages that link to this entity (what *other* articles say *about* it — best source for missing relationships and context)",
                context.Chunks.Where(c => c.Origin == ChunkOrigin.LinkingPage),
                linkedEntities
            );
            RenderChunkSection(
                sb,
                "Vector-similar passages from across the corpus (use only when they directly mention the target by name)",
                context.Chunks.Where(c => c.Origin == ChunkOrigin.VectorSimilar),
                linkedEntities
            );
        }

        sb.AppendLine("---");
        sb.AppendLine("Now produce the JSON proposal batch. ENHANCE this node by examining all four enhancement vectors (annotateEdges, fillGapEdges, nodeProposals, addEdges).");
        sb.AppendLine(
            "Fill each array only with proposals you can directly justify from the chunks/neighbours above. Empty arrays are correct when the evidence doesn't support enrichment in that vector."
        );
        sb.AppendLine("Prefer evidence from the target's own page over linking pages.");

        return sb.ToString();
    }

    /// <summary>
    /// Render one labelled chunk block in the user prompt, with chunkId + page context for citation.
    /// Produces no output when the source has no chunks (avoids empty headers cluttering the prompt).
    /// </summary>
    void RenderChunkSection(StringBuilder sb, string heading, IEnumerable<HolocronChunkSummary> chunks, IReadOnlyDictionary<string, LinkedEntity>? linkedEntities)
    {
        var list = chunks.ToList();
        if (list.Count == 0)
            return;

        sb.AppendFormat("### {0}\n\n", heading);
        foreach (var c in list)
        {
            sb.AppendFormat("**chunkId: {0}** (PageId={1}, page=\"{2}\", section: {3})\n", c.Id, c.PageId, c.Title, string.IsNullOrEmpty(c.Section) ? c.Heading : c.Section);
            sb.AppendLine(Truncate(c.Text, _settings.HolocronMaxChunkExcerptLength));

            // Surface the wiki-linked entities that appear inline in this chunk's text.
            // The agent can use any of these PageIds as a target for `addEdges` proposals
            // — e.g. when the chunk text mentions "[[bounty hunter]]" and that resolves
            // to a TitleOrPosition node, the agent can emit `has_role` to that PageId
            // instead of dumping the role-string into a property fieldPath.
            if (c.Links is { Count: > 0 } && linkedEntities is not null)
            {
                var resolved = c.Links.Select(url => linkedEntities.TryGetValue(url, out var e) ? e : null).Where(e => e is not null && e.PageId != c.PageId).Cast<LinkedEntity>().ToList();
                if (resolved.Count > 0)
                {
                    sb.AppendLine("Wiki entities linked in this chunk's text (use any of these PageIds as `addEdges` targets when the chunk evidences a relationship):");
                    foreach (var e in resolved.Take(20))
                    {
                        sb.AppendFormat("  - PageId={0}, Name=\"{1}\", Type={2}\n", e.PageId, e.Name, e.Type);
                    }
                    if (resolved.Count > 20)
                        sb.AppendFormat("  …(+ {0} more, omitted for brevity)\n", resolved.Count - 20);
                }
            }
            sb.AppendLine();
        }
    }

    static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    // ── Pre-flight + apply ────────────────────────────────────────────────

    async Task<(int Created, int EvidenceFailures)> ApplyProposalsAsync(HolocronContext context, HolocronProposalsBatch batch, string triggeredBy, CancellationToken ct)
    {
        // Snapshot the universe once: existing edges (for Annotate/FillGap target lookup)
        // and Active edge enrichments (for Add pair-already-connected detection).
        //
        // Two key sets per source: (a) the (from, to, label) tuple — used by Annotate
        // and FillGap to confirm the targeted edge exists; (b) the unordered node-pair —
        // used by Add to reject ANY edge between the same pair regardless of label or
        // direction. The unordered pair is the load-bearing rule against semantic
        // duplicates: the agent had a habit of proposing `member_of` next to an existing
        // `affiliated_with` between the same pair (different label = bypassed an early
        // tuple-only check but produced two parallel edges in the graph viewer).
        // See Design-018 v1 policy.
        var existingEdgeKeys = context.OutgoingEdges.Concat(context.IncomingEdges).Select(e => EdgeKey(e.FromId, e.ToId, e.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNodePairs = context.OutgoingEdges.Concat(context.IncomingEdges).Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingActiveEdgeEnrichments = await _edgeEnrichments
            .Find(
                Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active)
                    & (Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, context.Node.PageId) | Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, context.Node.PageId))
            )
            .ToListAsync(ct);
        var enrichmentNodePairs = existingActiveEdgeEnrichments.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate chunk citations once: pull every chunkId mentioned across all four
        // arrays and confirm they exist in search.chunks. The four evidence record types
        // are structurally identical — see records section for the schema-gen rationale —
        // so we project each through the common (chunkId, pageId) shape and union them.
        var allEvidenceCitations = batch
            .NodeProposals.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId)))
            .Concat(batch.AnnotateEdges.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId))))
            .Concat(batch.FillGapEdges.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId))))
            .Concat(batch.AddEdges.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId))))
            .ToList();

        // Filter chunk IDs to well-formed 24-char ObjectId hex strings before the Mongo
        // lookup. ArticleChunk.Id has [BsonRepresentation(BsonType.ObjectId)], so the
        // driver converts each string to ObjectId during Filter.In serialization — a
        // malformed value (the agent has been observed emitting 25-char strings)
        // throws a FormatException that crashes the whole enhancement. Treat malformed
        // chunkIds as evidence-validation failures the same way we'd treat a non-existent
        // chunkId: drop the proposal silently downstream.
        var citedChunkIds = allEvidenceCitations.Select(c => c.ChunkId).Where(id => !string.IsNullOrEmpty(id) && ObjectId.TryParse(id, out _)).Distinct().ToList();
        var validChunkIds =
            citedChunkIds.Count == 0 ? new HashSet<string>() : new HashSet<string>(await _chunks.Find(Builders<ArticleChunk>.Filter.In(c => c.Id, citedChunkIds!)).Project(c => c.Id).ToListAsync(ct));

        var citedPageIds = allEvidenceCitations.Select(c => c.SourcePageId).Where(id => id is not null and not 0).Select(id => id!.Value).Distinct().ToList();
        var validPageIds =
            citedPageIds.Count == 0 ? new HashSet<int>() : new HashSet<int>(await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, citedPageIds)).Project(n => n.PageId).ToListAsync(ct));

        var nodeInserts = new List<NodeEnrichment>();
        var edgeInserts = new List<EdgeEnrichment>();
        var events = new List<HolocronEvent>();
        var evidenceFailures = 0;

        // ── Node proposals ─────────────────────────────────────────────────
        // The server picks Add vs Augment based on whether the field already has values.
        // The agent never sees an "operation" choice, so it can't get it wrong.
        foreach (var prop in batch.NodeProposals)
        {
            if (!ValidateEvidence(prop.Evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(prop.FieldPath))
                continue;
            if (prop.Values is null || prop.Values.Count == 0)
                continue;

            // Strip "properties." prefix if present — agent may emit either form.
            var key = prop.FieldPath.StartsWith("properties.", StringComparison.OrdinalIgnoreCase) ? prop.FieldPath["properties.".Length..] : prop.FieldPath;
            var hasExisting = context.Node.Properties.TryGetValue(key, out var existing) && existing is not null && existing.Count > 0;

            EnrichmentOperation op;
            List<string> finalValues;
            if (hasExisting)
            {
                // Augment: dedupe against existing list (case-insensitive). Skip if all
                // proposed values were already present.
                finalValues = prop.Values.Where(v => !string.IsNullOrWhiteSpace(v) && !existing!.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
                if (finalValues.Count == 0)
                    continue;
                op = EnrichmentOperation.Augment;
            }
            else
            {
                finalValues = prop.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (finalValues.Count == 0)
                    continue;
                op = EnrichmentOperation.Add;
            }

            var doc = new NodeEnrichment
            {
                PageId = context.Node.PageId,
                FieldPath = key,
                Operation = op,
                Value = ToBsonValue(finalValues),
                Claim = prop.Claim,
                Evidence = prop.Evidence!.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList(),
                LlmReasoning = prop.Reasoning,
                ContentHashAtCreation = context.Node.ContentHash!,
                Status = EnrichmentStatus.Active,
                AppliedAt = DateTime.UtcNow,
                AgentVersion = AgentVersion,
                ModelId = _settings.HolocronModel,
            };
            nodeInserts.Add(doc);
            events.Add(
                new HolocronEvent
                {
                    EventType = HolocronEventType.EnrichmentCreated,
                    EnrichmentId = doc.Id,
                    PageId = doc.PageId,
                    FieldPath = doc.FieldPath,
                    Summary = $"Holocron {op.ToString().ToLowerInvariant()} `{doc.FieldPath}` on {context.Node.Name}: {Truncate(doc.Claim, 200)}",
                    TriggeredBy = triggeredBy,
                    AgentVersion = AgentVersion,
                }
            );
        }

        // ── Annotate edge proposals ────────────────────────────────────────
        foreach (var prop in batch.AnnotateEdges)
        {
            if (!ValidateEvidence(prop.Evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsAnnotateValid(context, prop, existingEdgeKeys))
                continue;

            var fromHash = await ResolveEndpointHashAsync(context, prop.FromId, ct);
            var toHash = await ResolveEndpointHashAsync(context, prop.ToId, ct);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (!string.IsNullOrWhiteSpace(prop.Role))
                value["role"] = prop.Role.Trim();
            if (!string.IsNullOrWhiteSpace(prop.Qualifier))
                value["qualifier"] = prop.Qualifier.Trim();
            if (!string.IsNullOrWhiteSpace(prop.Description))
                value["description"] = prop.Description.Trim();

            var doc = BuildEdgeEnrichment(
                prop.FromId,
                prop.ToId,
                prop.Label,
                EnrichmentOperation.Annotate,
                value,
                prop.Claim,
                prop.Evidence!.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList(),
                prop.Reasoning,
                fromHash,
                toHash
            );
            edgeInserts.Add(doc);
            events.Add(BuildEdgeCreatedEvent(doc, EnrichmentOperation.Annotate, triggeredBy));
        }

        // ── FillGap edge proposals ─────────────────────────────────────────
        foreach (var prop in batch.FillGapEdges)
        {
            if (!ValidateEvidence(prop.Evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsFillGapValid(context, prop, existingEdgeKeys))
                continue;

            var fromHash = await ResolveEndpointHashAsync(context, prop.FromId, ct);
            var toHash = await ResolveEndpointHashAsync(context, prop.ToId, ct);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (prop.FromYear.HasValue)
                value["fromYear"] = prop.FromYear.Value;
            if (prop.ToYear.HasValue)
                value["toYear"] = prop.ToYear.Value;

            var doc = BuildEdgeEnrichment(
                prop.FromId,
                prop.ToId,
                prop.Label,
                EnrichmentOperation.FillGap,
                value,
                prop.Claim,
                prop.Evidence!.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList(),
                prop.Reasoning,
                fromHash,
                toHash
            );
            edgeInserts.Add(doc);
            events.Add(BuildEdgeCreatedEvent(doc, EnrichmentOperation.FillGap, triggeredBy));
        }

        // ── Add edge proposals ─────────────────────────────────────────────
        foreach (var prop in batch.AddEdges)
        {
            if (!ValidateEvidence(prop.Evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsAddEdgeValid(context, prop, existingNodePairs, enrichmentNodePairs))
                continue;

            var fromHash = await ResolveEndpointHashAsync(context, prop.FromId, ct);
            var toHash = await ResolveEndpointHashAsync(context, prop.ToId, ct);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (prop.FromYear.HasValue)
                value["fromYear"] = prop.FromYear.Value;
            if (prop.ToYear.HasValue)
                value["toYear"] = prop.ToYear.Value;
            if (prop.Weight.HasValue)
                value["weight"] = prop.Weight.Value;

            var doc = BuildEdgeEnrichment(
                prop.FromId,
                prop.ToId,
                prop.Label,
                EnrichmentOperation.Add,
                value,
                prop.Claim,
                prop.Evidence!.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList(),
                prop.Reasoning,
                fromHash,
                toHash
            );
            edgeInserts.Add(doc);
            events.Add(BuildEdgeCreatedEvent(doc, EnrichmentOperation.Add, triggeredBy));
        }

        if (nodeInserts.Count > 0)
            await _enrichments.InsertManyAsync(nodeInserts, cancellationToken: ct);
        if (edgeInserts.Count > 0)
            await _edgeEnrichments.InsertManyAsync(edgeInserts, cancellationToken: ct);
        if (events.Count > 0)
            await _events.InsertManyAsync(events, cancellationToken: ct);

        return (nodeInserts.Count + edgeInserts.Count, evidenceFailures);
    }

    EdgeEnrichment BuildEdgeEnrichment(
        int fromId,
        int toId,
        string label,
        EnrichmentOperation op,
        BsonDocument value,
        string claim,
        List<EnrichmentEvidence> evidence,
        string reasoning,
        string fromHash,
        string toHash
    ) =>
        new()
        {
            FromId = fromId,
            ToId = toId,
            Label = label,
            Operation = op,
            Value = value,
            Claim = claim,
            Evidence = evidence,
            LlmReasoning = reasoning,
            ContentHashAtCreation = $"{fromHash}|{toHash}",
            Status = EnrichmentStatus.Active,
            AppliedAt = DateTime.UtcNow,
            AgentVersion = AgentVersion,
            ModelId = _settings.HolocronModel,
        };

    static HolocronEvent BuildEdgeCreatedEvent(EdgeEnrichment doc, EnrichmentOperation op, string triggeredBy) =>
        new()
        {
            EventType = HolocronEventType.EdgeEnrichmentCreated,
            EnrichmentId = doc.Id,
            FromId = doc.FromId,
            ToId = doc.ToId,
            Label = doc.Label,
            Summary = $"Holocron {op.ToString().ToLowerInvariant()} edge `{doc.Label}` from {doc.FromId} to {doc.ToId}: {Truncate(doc.Claim, 200)}",
            TriggeredBy = triggeredBy,
            AgentVersion = AgentVersion,
        };

    async Task<string?> ResolveEndpointHashAsync(HolocronContext context, int pageId, CancellationToken ct) =>
        pageId == context.Node.PageId ? context.Node.ContentHash : await GetContentHashAsync(pageId, ct);

    static bool ValidateEvidence(IEnumerable<(int? SourcePageId, string? ChunkId)>? evidence, HashSet<int> validPageIds, HashSet<string> validChunkIds)
    {
        if (evidence is null)
            return false;
        // At least ONE evidence item must resolve to a real source.
        foreach (var ev in evidence)
        {
            if (ev.SourcePageId is { } pid && pid > 0 && validPageIds.Contains(pid))
                return true;
            if (!string.IsNullOrEmpty(ev.ChunkId) && validChunkIds.Contains(ev.ChunkId))
                return true;
        }
        return false;
    }

    bool IsAnnotateValid(HolocronContext context, AnnotateEdgeProposal prop, HashSet<string> existingEdgeKeys)
    {
        if (prop.FromId <= 0 || prop.ToId <= 0 || string.IsNullOrWhiteSpace(prop.Label))
            return false;
        if (prop.FromId != context.Node.PageId && prop.ToId != context.Node.PageId)
            return false;
        // Annotate targets an existing edge — the agent must have copied (fromId, toId, label)
        // from the candidate list. If not, reject (the user prompt told them the rule).
        if (!existingEdgeKeys.Contains(EdgeKey(prop.FromId, prop.ToId, prop.Label)))
            return false;
        // At least one of role / qualifier / description must carry signal.
        return !string.IsNullOrWhiteSpace(prop.Role) || !string.IsNullOrWhiteSpace(prop.Qualifier) || !string.IsNullOrWhiteSpace(prop.Description);
    }

    bool IsFillGapValid(HolocronContext context, FillGapEdgeProposal prop, HashSet<string> existingEdgeKeys)
    {
        if (prop.FromId <= 0 || prop.ToId <= 0 || string.IsNullOrWhiteSpace(prop.Label))
            return false;
        if (prop.FromId != context.Node.PageId && prop.ToId != context.Node.PageId)
            return false;
        if (!existingEdgeKeys.Contains(EdgeKey(prop.FromId, prop.ToId, prop.Label)))
            return false;
        if (!prop.FromYear.HasValue && !prop.ToYear.HasValue)
            return false;
        // At least one proposed bound must be applicable to the existing edge:
        //   - filling a NULL bound, OR
        //   - refining a Lifecycle/Unknown-sourced bound (Phase 5 lifecycle fallback,
        //     soft upper bound — see Design-021).
        // Bounds tagged Infobox are hard — never overwrite them.
        return context
            .OutgoingEdges.Concat(context.IncomingEdges)
            .Where(e => e.FromId == prop.FromId && e.ToId == prop.ToId && string.Equals(e.Label, prop.Label, StringComparison.OrdinalIgnoreCase))
            .Any(e => (prop.FromYear.HasValue && IsBoundRefinable(e, isFrom: true)) || (prop.ToYear.HasValue && IsBoundRefinable(e, isFrom: false)));
    }

    /// <summary>
    /// True when the targeted bound on <paramref name="edge"/> is either null or
    /// explicitly tagged <see cref="EdgeBoundsSource.Lifecycle"/>. Untagged
    /// (<see cref="EdgeBoundsSource.Unknown"/>) bounds with a non-null value are
    /// treated as **hard**: we don't know how they were derived, so the safe
    /// assumption is they came from the infobox and must not be overwritten.
    /// Migration 0015 retroactively tags every existing edge so the Unknown
    /// case shouldn't survive the next deploy. See Design-021.
    /// </summary>
    static bool IsBoundRefinable(RelationshipEdge edge, bool isFrom)
    {
        var existing = isFrom ? edge.FromYear : edge.ToYear;
        if (!existing.HasValue)
            return true;
        var src = edge.Meta?.BoundsSource ?? EdgeBoundsSource.Unknown;
        return src is EdgeBoundsSource.Lifecycle;
    }

    /// <summary>True when <paramref name="edge"/> has at least one refinable bound (null or Lifecycle/Unknown).</summary>
    static bool IsFillGapCandidate(RelationshipEdge edge) => IsBoundRefinable(edge, isFrom: true) || IsBoundRefinable(edge, isFrom: false);

    /// <summary>
    /// Format a single bound for the FillGap candidate list, annotating its provenance so
    /// the agent can tell hard infobox bounds from soft lifecycle-derived ones.
    /// </summary>
    static string FormatBoundForPrompt(int? year, EdgeBoundsSource? source)
    {
        if (!year.HasValue)
            return "null";
        return (source ?? EdgeBoundsSource.Unknown) switch
        {
            EdgeBoundsSource.Infobox => $"{year.Value} [infobox, hard]",
            EdgeBoundsSource.Lifecycle => $"{year.Value} [lifecycle, refinable]",
            EdgeBoundsSource.Holocron => $"{year.Value} [holocron]",
            // Unknown = un-migrated edge from before Design-021. Treat as hard until
            // Migration 0015 / next Phase 5 run gives it an explicit tag.
            _ => $"{year.Value} [unknown, hard]",
        };
    }

    /// <summary>
    /// Compact "what's fillable on this edge" hint for the prompt — translates the bound
    /// states into a short instruction so the agent doesn't have to reason about the
    /// matrix of (null vs lifecycle vs infobox) × (from vs to) cases itself.
    /// </summary>
    static string DescribeFillScope(RelationshipEdge edge)
    {
        var fromRefinable = IsBoundRefinable(edge, isFrom: true);
        var toRefinable = IsBoundRefinable(edge, isFrom: false);
        return (fromRefinable, toRefinable) switch
        {
            (true, true) => "fill or refine either or both",
            (true, false) => "fill or refine fromYear only",
            (false, true) => "fill or refine toYear only",
            _ => "(unreachable — both bounds hard)",
        };
    }

    bool IsAddEdgeValid(HolocronContext context, AddEdgeProposal prop, HashSet<string> existingNodePairs, HashSet<string> enrichmentNodePairs)
    {
        if (prop.FromId <= 0 || prop.ToId <= 0 || string.IsNullOrWhiteSpace(prop.Label))
            return false;
        if (prop.FromId != context.Node.PageId && prop.ToId != context.Node.PageId)
            return false;
        // Label must be in the canonical registry — no inventing synonyms. Add is the
        // only path that introduces a new label on a new edge, so this check is the
        // load-bearing safety net.
        if (!_knownLabels.Contains(prop.Label))
        {
            _logger.LogInformation("HolocronAgent: rejecting Add edge — label `{Label}` is not in the canonical registry. Proposed: {FromId} -> {ToId}", prop.Label, prop.FromId, prop.ToId);
            return false;
        }
        // Pair must have NO existing edge in either direction with any label, and no
        // active Add-edge enrichment between the same pair. See class comment on the
        // unordered-pair rule.
        var pair = NodePairKey(prop.FromId, prop.ToId);
        return !existingNodePairs.Contains(pair) && !enrichmentNodePairs.Contains(pair);
    }

    static string EdgeKey(int from, int to, string label) => $"{from}-{to}-{label.ToLowerInvariant()}";

    /// <summary>
    /// Direction-agnostic node-pair key: <c>min(a,b)-max(a,b)</c>. Two edges with the
    /// same pair of endpoints (in either direction) collapse to the same key. Used by
    /// the Add-edge pre-flight to reject any edge between an already-connected pair.
    /// </summary>
    static string NodePairKey(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

    static EnrichmentEvidence BuildEvidence(int? sourcePageId, string? chunkId, string excerpt, double? relevanceScore) =>
        new()
        {
            SourcePageId = sourcePageId ?? 0,
            ChunkId = chunkId,
            Excerpt = string.IsNullOrEmpty(excerpt) ? string.Empty : (excerpt.Length > 1000 ? excerpt[..1000] : excerpt),
            RelevanceScore = relevanceScore,
        };

    static BsonValue ToBsonValue(List<string> values) => values.Count == 1 ? new BsonString(values[0]) : new BsonArray(values);

    async Task<string?> GetContentHashAsync(int pageId, CancellationToken ct) => await _nodes.Find(n => n.PageId == pageId).Project(n => n.ContentHash).FirstOrDefaultAsync(ct);

    // ── Bulk update helpers for staleness sweep ───────────────────────────

    static UpdateOneModel<T> BuildStaleUpdate<T>(string id)
        where T : class
    {
        // Bson-level update keeps T-agnostic (NodeEnrichment + EdgeEnrichment share the same shape).
        var filter = new BsonDocumentFilterDefinition<T>(new BsonDocument("_id", ObjectId.Parse(id)));
        var update = new BsonDocumentUpdateDefinition<T>(new BsonDocument("$set", new BsonDocument { { "status", "Stale" } }));
        return new UpdateOneModel<T>(filter, update);
    }

    static HolocronEvent BuildStaleEvent(NodeEnrichment en, HolocronEventType type, string reason) =>
        new()
        {
            EventType = type,
            EnrichmentId = en.Id,
            PageId = en.PageId,
            FieldPath = en.FieldPath,
            Summary = $"Enrichment for `{en.FieldPath}` on PageId={en.PageId} marked stale: {reason}.",
            TriggeredBy = "phase1-staleness",
            AgentVersion = AgentVersion,
        };

    static HolocronEvent BuildStaleEdgeEvent(EdgeEnrichment en, HolocronEventType type, string reason) =>
        new()
        {
            EventType = type,
            EnrichmentId = en.Id,
            FromId = en.FromId,
            ToId = en.ToId,
            Label = en.Label,
            Summary = $"Edge enrichment `{en.Label}` ({en.FromId}→{en.ToId}) marked stale: {reason}.",
            TriggeredBy = "phase1-staleness",
            AgentVersion = AgentVersion,
        };

    // ── Event bookkeeping ─────────────────────────────────────────────────

    /// <summary>Insert a single event into the audit log. Never updates existing events.</summary>
    Task EmitEventAsync(HolocronEvent evt, CancellationToken ct) => _events.InsertOneAsync(evt, cancellationToken: ct);

    // ── Internal records ──────────────────────────────────────────────────

    sealed record HolocronContext(
        GraphNode Node,
        List<RelationshipEdge> OutgoingEdges,
        List<RelationshipEdge> IncomingEdges,
        List<HolocronNeighbourSummary> Neighbours,
        List<HolocronChunkSummary> Chunks,
        List<HolocronLabelSummary> CanonicalLabels
    );

    sealed record HolocronNeighbourSummary(int PageId, string Name, string Type, int? StartYear, int? EndYear);

    /// <summary>
    /// Edge-label vocabulary surfaced to the agent. Top-K from <c>kg.labels</c>
    /// where <c>fromTypes</c> contains the target node's type, sorted by usage count.
    /// The agent must pick a label from this list — pre-flight rejects anything else.
    /// </summary>
    sealed record HolocronLabelSummary(string Label, string Reverse, string Description, int UsageCount);

    sealed record HolocronChunkSummary(string Id, int PageId, string Title, string Heading, string Section, string Text, ChunkOrigin Origin, List<string> Links);

    /// <summary>
    /// Where a context chunk came from. Surfaced in the user prompt so the agent
    /// can weight its evidence — own-page text is highest authority for what the
    /// wiki asserts about the target; linking-page text shows the target from
    /// other perspectives; vector-similar can be tangential.
    /// </summary>
    enum ChunkOrigin
    {
        OwnPage,
        LinkingPage,
        VectorSimilar,
    }

    /// <summary>
    /// Top-level structured-output target. Each operation has its own typed array — the
    /// agent doesn't pick an operation string, it just fills in whichever buckets apply
    /// to the candidates surfaced in the user prompt. This is the schema-level enforcement
    /// of the operation hierarchy: a model emitting an <see cref="AnnotateEdgeProposal"/>
    /// cannot accidentally claim "Add" semantics, because there's no string operation
    /// field to disagree with. The previous shared <c>HolocronEdgeProposal</c> with a
    /// <c>string operation</c> field was the source of most pre-flight rejections.
    /// </summary>
    public sealed record HolocronProposalsBatch(
        [property: JsonPropertyName("nodeProposals")] List<HolocronNodeProposal> NodeProposals,
        [property: JsonPropertyName("annotateEdges")] List<AnnotateEdgeProposal> AnnotateEdges,
        [property: JsonPropertyName("fillGapEdges")] List<FillGapEdgeProposal> FillGapEdges,
        [property: JsonPropertyName("addEdges")] List<AddEdgeProposal> AddEdges
    );

    /// <summary>
    /// Node property enrichment. The server decides Add vs Augment based on whether the
    /// field already has values: if not, every value becomes an Add; if so, only values
    /// not already present are kept and the operation is Augment. The agent just provides
    /// "this field should contain these values" with evidence — picking the operation is
    /// not the agent's job.
    /// </summary>
    public sealed record HolocronNodeProposal(
        [property: JsonPropertyName("fieldPath")] string FieldPath,
        [property: JsonPropertyName("values")] List<string> Values,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<NodeProposalEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    /// <summary>
    /// Annotate an existing edge with role / qualifier / description context. The agent
    /// MUST pick (fromId, toId, label) from the "Edges available for ANNOTATE" section
    /// of the user prompt — pre-flight verifies the edge exists and at least one of
    /// role / qualifier / description is non-empty. Uses a distinct evidence record type
    /// to keep the JSON schema $ref-free at depth (see <see cref="NodeProposalEvidence"/>).
    /// </summary>
    public sealed record AnnotateEdgeProposal(
        [property: JsonPropertyName("fromId")] int FromId,
        [property: JsonPropertyName("toId")] int ToId,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("qualifier")] string? Qualifier,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<AnnotateEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    /// <summary>
    /// Fill null temporal bounds (<c>fromYear</c> / <c>toYear</c>) on an existing edge.
    /// Pre-flight verifies the edge exists with a null in at least one of the targeted
    /// bounds.
    /// </summary>
    public sealed record FillGapEdgeProposal(
        [property: JsonPropertyName("fromId")] int FromId,
        [property: JsonPropertyName("toId")] int ToId,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("fromYear")] int? FromYear,
        [property: JsonPropertyName("toYear")] int? ToYear,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<FillGapEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    /// <summary>
    /// Add a brand-new edge between two nodes that have NO existing connection (in any
    /// direction, with any label). Last resort — most relationships should already be
    /// captured by Phase 1 infobox extraction. Pre-flight verifies (a) the label is in
    /// the canonical registry, (b) no edge currently connects the pair.
    /// </summary>
    public sealed record AddEdgeProposal(
        [property: JsonPropertyName("fromId")] int FromId,
        [property: JsonPropertyName("toId")] int ToId,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("fromYear")] int? FromYear,
        [property: JsonPropertyName("toYear")] int? ToYear,
        [property: JsonPropertyName("weight")] double? Weight,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<AddEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    // Distinct evidence record types per proposal type. Identical shapes — the schema
    // generator must inline each instance rather than emit a shared $ref, because
    // OpenAI strict mode rejects $refs deeper than the top-level $defs. A shared
    // EdgeProposalEvidence type produced refs at depth 6, which the API rejected with
    // invalid_json_schema. Mapping helpers (BuildEvidence) project all four shapes
    // through a common (sourcePageId, chunkId, excerpt, relevanceScore) tuple.
    public sealed record NodeProposalEvidence(
        [property: JsonPropertyName("sourcePageId")] int? SourcePageId,
        [property: JsonPropertyName("chunkId")] string? ChunkId,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("relevanceScore")] double? RelevanceScore
    );

    public sealed record AnnotateEvidence(
        [property: JsonPropertyName("sourcePageId")] int? SourcePageId,
        [property: JsonPropertyName("chunkId")] string? ChunkId,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("relevanceScore")] double? RelevanceScore
    );

    public sealed record FillGapEvidence(
        [property: JsonPropertyName("sourcePageId")] int? SourcePageId,
        [property: JsonPropertyName("chunkId")] string? ChunkId,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("relevanceScore")] double? RelevanceScore
    );

    public sealed record AddEvidence(
        [property: JsonPropertyName("sourcePageId")] int? SourcePageId,
        [property: JsonPropertyName("chunkId")] string? ChunkId,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("relevanceScore")] double? RelevanceScore
    );
}

/// <summary>Summary of one staleness sweep — counts of enrichments transitioned.</summary>
public sealed record StalenessSweepSummary(int MarkedStale, int MarkedOrphaned, int Total);

/// <summary>Summary of one <see cref="HolocronAgent.EnhanceNodeAsync"/> call.</summary>
public sealed record NodeEnhancementSummary(int PageId, int EnrichmentsCreated, int EvidenceFailures);
