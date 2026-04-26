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
    public const string AgentVersion = "holocron-v1.0.0";

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
                .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.OwnPage))
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
                .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.LinkingPage))
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
                var vectorChunks = hits.Where(h => !coveredPageIds.Contains(h.PageId) && !string.IsNullOrEmpty(h.ChunkId))
                    .Take(vectorLimit)
                    .Select(h => new HolocronChunkSummary(h.ChunkId, h.PageId, h.Title, h.Heading, h.Section, h.Text, ChunkOrigin.VectorSimilar))
                    .ToList();
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

    // ── LLM call ──────────────────────────────────────────────────────────

    async Task<HolocronProposalsBatch?> CallLlmForProposalsAsync(HolocronContext context, CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(context);

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

            The graph is built from Wookieepedia infobox extraction. That extraction is the
            canonical truth foundation. Your job is to polish around the edges by adding
            information the infobox didn't capture, citing real sources every time.

            ## How proposals work

            The output schema has FOUR arrays. Each array corresponds to one kind of work, and
            the candidate set for each is pre-computed and listed in the user prompt. You do
            NOT pick an operation name — you just fill in the array that matches what you
            found. If a candidate doesn't apply, leave the array empty.

            1. `annotateEdges` — **primary work.** For each edge in the user prompt's
               "Edges available for ANNOTATE" list, decide whether the article chunks reveal
               role / qualifier / description context worth attaching. Reference (fromId, toId,
               label) verbatim from that list and supply at least one of role / qualifier /
               description.
            2. `fillGapEdges` — for each edge in the user prompt's "Edges with null temporal
               bounds — FillGap candidates" list, fill in `fromYear` and/or `toYear` if the
               chunks let you cite a year. Reference (fromId, toId, label) from that list.
            3. `nodeProposals` — append a new value or fill a missing property on the target
               node. The server decides Add (no existing values) vs Augment (existing list)
               based on the current state — you just give the field path and the values you
               want present.
            4. `addEdges` — **last resort.** Only if a clearly important relationship
               between two nodes has NO existing edge in either direction (you saw NO matching
               entry in the Annotate or FillGap candidate lists for the pair). Most missing
               relationships were already captured by Phase 1; if you find yourself reaching
               for `addEdges`, double-check that the pair really has no edge.

            Priority is: annotateEdges > fillGapEdges > nodeProposals > addEdges. Most runs
            should produce mostly Annotate work and rarely if ever an Add edge.

            ## Hard rules

            - Never propose a value that contradicts the infobox. If a property already has a
              value, do not touch it.
            - For `annotateEdges` and `fillGapEdges`: (fromId, toId, label) MUST match an entry
              in the corresponding candidate list in the user prompt. Do not synthesise edges
              that aren't in the lists.
            - For `addEdges`: the (fromId, toId) pair must NOT appear in EITHER the Annotate or
              FillGap candidate lists, in either direction. If it does, use Annotate instead —
              even if your intended label differs from the existing one. Two parallel edges
              between the same pair are forbidden.
            - For `addEdges`: `label` MUST come from the canonical vocabulary listed under
              "Canonical edge labels". Do NOT invent synonyms (e.g. `member_of` next to an
              existing `affiliated_with`). If no canonical label fits, emit nothing for that
              edge.
            - Every proposal MUST cite at least one piece of evidence — either a `sourcePageId`
              (another KG node's PageId) or a `chunkId` (a wiki article chunk id) — with an
              excerpt taken verbatim from that source.
            - Cite only the article chunks and neighbour nodes provided in the user prompt.
              Do not invent sources.
            - Chunks come from THREE labelled sections in the user prompt:
              (1) the target's own page — high authority for what the wiki asserts about the entity,
              (2) pages that LINK TO the target — best source for missing relationship context,
              (3) vector-similar passages — useful when they mention the target by name.
            - When unsure or evidence is weak, emit nothing. Quality over quantity.
            - All four arrays may be empty. An empty result is correct when nothing is missing.

            ## Field cheat-sheet

            - `annotateEdges` items: `fromId`, `toId`, `label`, plus AT LEAST ONE of
              `role` (e.g. "Jedi General"), `qualifier` (e.g. "during the Clone Wars"),
              `description` (longer narrative). Plus `claim`, `evidence`, `reasoning`.
            - `fillGapEdges` items: `fromId`, `toId`, `label`, plus AT LEAST ONE of
              `fromYear`, `toYear`. Plus `claim`, `evidence`, `reasoning`.
            - `addEdges` items: `fromId`, `toId`, `label`, optionally `fromYear`, `toYear`,
              `weight`. Plus `claim`, `evidence`, `reasoning`.
            - `nodeProposals` items: `fieldPath`, `values` (list), `claim`, `evidence`,
              `reasoning`.
            """;

    string BuildUserPrompt(HolocronContext context)
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
            sb.Append("(none — every property is a candidate for Add)\n");
        }
        else
        {
            foreach (var (key, values) in node.Properties.OrderBy(p => p.Key))
            {
                sb.AppendFormat("- `{0}`: {1}\n", key, string.Join("; ", values.Take(8)));
            }
        }
        sb.Append('\n');

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

        // FillGap candidates: subset where at least one temporal bound is null. Pre-flight
        // requires the same — surfacing them as a focused list reduces the chance of the
        // agent proposing a fill on an edge that already has both bounds.
        var fillGapCandidates = allEdges.Where(e => !e.FromYear.HasValue || !e.ToYear.HasValue).ToList();
        sb.Append("## Edges with null temporal bounds — FillGap candidates (use these for the `fillGapEdges` array)\n\n");
        sb.Append("Pick `fromId`, `toId`, `label` verbatim from this list. Fill in `fromYear` and/or `toYear` only when the chunks let you cite a specific year — never guess.\n\n");
        if (fillGapCandidates.Count == 0)
        {
            sb.Append("(every edge already has both temporal bounds — FillGap has nothing to do this run)\n");
        }
        else
        {
            foreach (var e in fillGapCandidates)
            {
                var fromName = e.FromId == node.PageId ? node.Name : e.FromName;
                var toName = e.ToId == node.PageId ? node.Name : e.ToName;
                sb.AppendFormat(
                    "- fromId={0} ({1}) —[{2}]→ toId={3} ({4})  (fromYear={5}, toYear={6}; fill {7})\n",
                    e.FromId,
                    fromName,
                    e.Label,
                    e.ToId,
                    toName,
                    e.FromYear?.ToString() ?? "null",
                    e.ToYear?.ToString() ?? "null",
                    !e.FromYear.HasValue && !e.ToYear.HasValue ? "either or both"
                        : !e.FromYear.HasValue ? "fromYear"
                        : "toYear"
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
            // Same target/page authority hierarchy: target's own page > linking page > vector-similar.
            RenderChunkSection(sb, "Target's own page (highest authority for what the wiki asserts about this entity)", context.Chunks.Where(c => c.Origin == ChunkOrigin.OwnPage));
            RenderChunkSection(
                sb,
                "Pages that link to this entity (what *other* articles say *about* it — best source for missing relationships and context)",
                context.Chunks.Where(c => c.Origin == ChunkOrigin.LinkingPage)
            );
            RenderChunkSection(sb, "Vector-similar passages from across the corpus (use cautiously — may be tangentially related)", context.Chunks.Where(c => c.Origin == ChunkOrigin.VectorSimilar));
        }

        sb.AppendLine("---");
        sb.AppendLine("Now produce the JSON proposal batch. Fill in each of the four arrays only with proposals you can directly justify from the chunks/neighbours above.");
        sb.AppendLine("Annotate is the primary array — it's where most enrichment value lives. FillGap when chunks let you cite a year. AddEdges is a last resort.");
        sb.AppendLine("Prefer evidence from the target's own page or linking pages. Vector-similar chunks are useful when they directly mention the target by name.");

        return sb.ToString();
    }

    /// <summary>
    /// Render one labelled chunk block in the user prompt, with chunkId + page context for citation.
    /// Produces no output when the source has no chunks (avoids empty headers cluttering the prompt).
    /// </summary>
    void RenderChunkSection(StringBuilder sb, string heading, IEnumerable<HolocronChunkSummary> chunks)
    {
        var list = chunks.ToList();
        if (list.Count == 0)
            return;

        sb.AppendFormat("### {0}\n\n", heading);
        foreach (var c in list)
        {
            sb.AppendFormat("**chunkId: {0}** (PageId={1}, page=\"{2}\", section: {3})\n", c.Id, c.PageId, c.Title, string.IsNullOrEmpty(c.Section) ? c.Heading : c.Section);
            sb.AppendLine(Truncate(c.Text, _settings.HolocronMaxChunkExcerptLength));
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
        // At least one of the proposed bounds must be filling a NULL on the existing edge —
        // we never let FillGap overwrite a non-null bound.
        return context
            .OutgoingEdges.Concat(context.IncomingEdges)
            .Where(e => e.FromId == prop.FromId && e.ToId == prop.ToId && string.Equals(e.Label, prop.Label, StringComparison.OrdinalIgnoreCase))
            .Any(e => (prop.FromYear.HasValue && !e.FromYear.HasValue) || (prop.ToYear.HasValue && !e.ToYear.HasValue));
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

    sealed record HolocronChunkSummary(string Id, int PageId, string Title, string Heading, string Section, string Text, ChunkOrigin Origin);

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
