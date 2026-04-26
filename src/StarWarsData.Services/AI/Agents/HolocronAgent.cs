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
            "HolocronAgent: EnhanceNodeAsync — PageId={PageId} ({Name}) — proposed {NodeProp}+{EdgeProp}, applied {Applied}, evidence failures {EvFails}.",
            pageId,
            context.Node.Name,
            batch.NodeProposals.Count,
            batch.EdgeProposals.Count,
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

        // Source 2 — linking-page chunks. For each top incoming-edge source, pull chunks
        // where the target's name appears as a substring. This is what makes the cross-page
        // story work: when enhancing Yoda, we want chunks from Padmé's page that mention
        // Yoda, not just Yoda's own page.
        var linkingLimit = Math.Max(0, _settings.HolocronLinkingPageChunks);
        if (linkingLimit > 0 && inEdges.Count > 0 && !string.IsNullOrWhiteSpace(node.Name))
        {
            // Top-K linking pages by edge weight; cap at linkingLimit so we don't fan out further.
            var linkingPageIds = inEdges.Select(e => e.FromId).Distinct().Take(linkingLimit).ToList();
            var perPageLimit = Math.Max(1, linkingLimit / Math.Max(1, linkingPageIds.Count)) + 1;

            // Case-insensitive substring match on the chunk text. MongoSafe-escape the name in case
            // it contains regex metachars (e.g. "Obi-Wan", "R2-D2").
            var nameRegex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(node.Name), "i");
            var linkingFilter = Builders<ArticleChunk>.Filter.In(c => c.PageId, linkingPageIds) & Builders<ArticleChunk>.Filter.Regex(c => c.Text, nameRegex);
            var linkingChunks = await _chunks
                .Find(linkingFilter)
                .SortBy(c => c.PageId)
                .ThenBy(c => c.ChunkIndex)
                .Limit(linkingLimit * perPageLimit) // small overshoot — we'll take linkingLimit total below
                .Project(c => new HolocronChunkSummary(c.Id, c.PageId, c.Title, c.Heading, c.Section, c.Text, ChunkOrigin.LinkingPage))
                .ToListAsync(ct);

            // Take at most `perPageLimit` chunks per source page so one verbose article doesn't crowd out others.
            var groupedByPage = linkingChunks.GroupBy(c => c.PageId).SelectMany(g => g.Take(perPageLimit)).Take(linkingLimit).ToList();
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

            ## Your operations (the only four you may emit)

            1. **Add** — propose a value for a property or edge that DOES NOT exist. For edges,
               this means the two nodes have NO existing connection at all. **Last resort.**
               Most missing relationships should already be in the wiki infobox; if Holocron
               needs to Add an edge it usually means the wiki itself didn't enumerate it.
            2. **Augment** — append items to an existing list-valued property, where each new item
               does not already appear in that list.
            3. **FillGap** — fill a null sub-property on an existing edge. Canonical use: set
               `fromYear`/`toYear` on an edge whose temporal bounds are null. Cleaner / more
               precise temporal data is high-value enrichment.
            4. **Annotate** — attach role / qualifier / description context to an EXISTING edge
               with the right canonical label, WITHOUT creating a parallel edge. Use this when
               the relationship is correct but under-specified — e.g. Obi-Wan `affiliated_with`
               Galactic Republic exists, and you want to record that he served the role
               "Jedi General during the Clone Wars commanding the 212th Attack Battalion".
               Annotate is the PRIMARY operation for edges that already exist.

            ## Priority order (most preferred → least)

            1. **Annotate existing edges** with richer context (role / qualifier / description).
               This is where most of your value lies — Phase 1 extraction has the relationship
               but rarely the narrative context around it.
            2. **FillGap** on temporal bounds of existing edges (`fromYear` / `toYear`).
            3. **Add** new edges only when ABSOLUTELY required — i.e. two nodes that are
               clearly related per the article text but have NO edge yet. If any edge already
               connects the pair, use Annotate instead.

            ## Hard rules

            - Never propose a value that contradicts the infobox. If the property already has a
              value, do not touch it.
            - Never propose a NEW edge (Add) between two nodes that are ALREADY connected by ANY
              edge — even if your proposed label is different. The relationship is already
              represented; the pre-flight will reject it. Use **Annotate** to attach role /
              qualifier / description context to the existing edge instead. That's the
              compromise: zero parallel lines in the graph viewer, richer information surfaced
              on hover/expand.
            - Edge labels MUST come from the canonical list provided in the user prompt under
              "Canonical edge labels". Do NOT invent new labels. Do NOT coin synonyms. If the
              relationship you want to express maps to one of the existing labels, use that
              label exactly as written. If no canonical label fits the relationship, emit
              nothing for that edge — Holocron does not introduce new vocabulary; that's
              the wiki's job (via FieldSemantics → Phase 1). The pre-flight will reject any
              label not in the canonical set.
            - Every proposal MUST cite at least one piece of evidence — either a sourcePageId
              (another KG node's PageId) or a chunkId (a wiki article chunk id) — with an excerpt
              taken verbatim from the source.
            - Cite from the article chunks and neighbour nodes provided. Do not invent sources.
            - The chunks come from THREE sources, listed in the user prompt under labelled sections:
              (1) the target's own page — high authority for what the wiki already asserts about it,
              (2) pages that LINK TO the target — best source for missing relationships and cross-references,
              (3) vector-similar passages from across the corpus — useful when they mention the target
                  by name, otherwise prefer (1) and (2).
            - When unsure or evidence is weak, emit nothing. Quality over quantity.
            - You may propose 0 enrichments. An empty result is correct when nothing is missing.

            ## Output

            Return a single JSON object matching the schema. Two arrays:
            - `nodeProposals` — Add or Augment to the target node's properties
            - `edgeProposals` — Annotate an existing edge with context (role/qualifier/description),
              FillGap on an existing edge's temporal bounds, or (last resort) Add a missing edge

            Each proposal has a one-sentence claim, a list of evidence excerpts, and a short
            reasoning string.

            ## Edge-proposal field cheat-sheet

            All edge proposals require: `operation`, `fromId`, `toId`, `label`, `claim`, `evidence`, `reasoning`.

            - `Add`      → optionally `fromYear`, `toYear`, `weight`. Leave role/qualifier/description null.
            - `FillGap`  → set `fromYear` and/or `toYear`. Leave role/qualifier/description null.
            - `Annotate` → set at least one of `role`, `qualifier`, `description`. Leave fromYear/toYear/weight null.
              `role` = e.g. "Jedi General" / "Senator" / "Master". `qualifier` = e.g. "during the Clone Wars".
              `description` = longer narrative that doesn't fit role/qualifier.
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

        sb.Append("## Existing outgoing edges — Annotate or FillGap these instead of proposing a new edge between the same pair\n\n");
        if (context.OutgoingEdges.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (var e in context.OutgoingEdges)
            {
                sb.AppendFormat("- {0} —[{1}]→ {2} (toId={3}, fromYear={4}, toYear={5}", node.Name, e.Label, e.ToName, e.ToId, e.FromYear?.ToString() ?? "null", e.ToYear?.ToString() ?? "null");
                if (e.Meta is not null && (!string.IsNullOrWhiteSpace(e.Meta.Qualifier) || !string.IsNullOrWhiteSpace(e.Meta.RawValue)))
                    sb.AppendFormat(", existing qualifier=\"{0}\"", Truncate(e.Meta.Qualifier ?? e.Meta.RawValue ?? string.Empty, 80));
                sb.Append(")\n");
            }
        }
        sb.Append('\n');

        sb.Append("## Existing incoming edges — same Annotate/FillGap rule applies\n\n");
        if (context.IncomingEdges.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (var e in context.IncomingEdges)
            {
                sb.AppendFormat("- {0} (fromId={1}) —[{2}]→ {3}\n", e.FromName, e.FromId, e.Label, node.Name);
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
        sb.AppendLine("Now produce the JSON proposal batch. Remember: only Add / Augment / FillGap, every proposal cites real evidence, do not contradict the infobox.");
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
        // Snapshot the universe once: the source node we already have, plus
        // existing edges and Active edge enrichments for duplicate detection.
        //
        // We track TWO key sets per source: (a) the (from, to, label) tuple — used by
        // FillGap to confirm the targeted edge exists; (b) the unordered node-pair —
        // used by Add to reject ANY edge between the same two nodes regardless of label
        // or direction. The unordered pair is the load-bearing rule against semantic
        // duplicates: the agent had a habit of proposing `member_of` next to an existing
        // `affiliated_with` between the same pair (different label = passed the v1 check
        // but produced two parallel edges in the graph viewer). See Design-018 v1 policy.
        var existingEdgeKeys = context.OutgoingEdges.Concat(context.IncomingEdges).Select(e => EdgeKey(e.FromId, e.ToId, e.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNodePairs = context.OutgoingEdges.Concat(context.IncomingEdges).Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingActiveEdgeEnrichments = await _edgeEnrichments
            .Find(
                Builders<EdgeEnrichment>.Filter.Eq(e => e.Status, EnrichmentStatus.Active)
                    & (Builders<EdgeEnrichment>.Filter.Eq(e => e.FromId, context.Node.PageId) | Builders<EdgeEnrichment>.Filter.Eq(e => e.ToId, context.Node.PageId))
            )
            .ToListAsync(ct);
        var enrichmentEdgeKeys = existingActiveEdgeEnrichments.Select(e => EdgeKey(e.FromId, e.ToId, e.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enrichmentNodePairs = existingActiveEdgeEnrichments.Select(e => NodePairKey(e.FromId, e.ToId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate chunk citations once: pull every chunkId mentioned across all proposals
        // and confirm they exist in search.chunks. Node and edge evidence types are distinct
        // (see schema-gen rationale on the records); project both to a common (chunkId, pageId)
        // shape so the lookup batches across them.
        var allEvidenceCitations = batch
            .NodeProposals.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId)))
            .Concat(batch.EdgeProposals.SelectMany(p => p.Evidence.Select(e => (e.ChunkId, e.SourcePageId))))
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

        foreach (var prop in batch.NodeProposals)
        {
            if (!ValidateNodeEvidence(prop.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsNodePropertyProposalValid(context.Node, prop))
                continue;

            var doc = new NodeEnrichment
            {
                PageId = context.Node.PageId,
                FieldPath = prop.FieldPath,
                Operation = ParseOperation(prop.Operation) ?? EnrichmentOperation.Add,
                Value = ToBsonValue(prop.Values),
                Claim = prop.Claim,
                Evidence = MapNodeEvidence(prop.Evidence),
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
                    Summary = $"Holocron {doc.Operation.ToString().ToLowerInvariant()} `{doc.FieldPath}` on {context.Node.Name}: {Truncate(doc.Claim, 200)}",
                    TriggeredBy = triggeredBy,
                    AgentVersion = AgentVersion,
                }
            );
        }

        foreach (var prop in batch.EdgeProposals)
        {
            if (!ValidateEdgeEvidence(prop.Evidence, validPageIds, validChunkIds))
            {
                evidenceFailures++;
                continue;
            }
            if (!IsEdgeProposalValid(context, prop, existingEdgeKeys, existingNodePairs, enrichmentEdgeKeys, enrichmentNodePairs))
                continue;

            var operation = ParseOperation(prop.Operation) ?? EnrichmentOperation.Add;

            // Combined hash for endpoints — needed by the staleness sweep
            var fromHash = prop.FromId == context.Node.PageId ? context.Node.ContentHash! : await GetContentHashAsync(prop.FromId, ct);
            var toHash = prop.ToId == context.Node.PageId ? context.Node.ContentHash! : await GetContentHashAsync(prop.ToId, ct);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (prop.FromYear.HasValue)
                value["fromYear"] = prop.FromYear.Value;
            if (prop.ToYear.HasValue)
                value["toYear"] = prop.ToYear.Value;
            if (prop.Weight.HasValue)
                value["weight"] = prop.Weight.Value;
            // Annotate-specific fields. Trimmed to avoid storing whitespace-only values
            // — IsEdgeProposalValid only requires ONE of these to be non-empty for Annotate
            // to pass, but all three may carry signal worth persisting if present.
            if (!string.IsNullOrWhiteSpace(prop.Role))
                value["role"] = prop.Role.Trim();
            if (!string.IsNullOrWhiteSpace(prop.Qualifier))
                value["qualifier"] = prop.Qualifier.Trim();
            if (!string.IsNullOrWhiteSpace(prop.Description))
                value["description"] = prop.Description.Trim();

            var doc = new EdgeEnrichment
            {
                FromId = prop.FromId,
                ToId = prop.ToId,
                Label = prop.Label,
                Operation = operation,
                Value = value,
                Claim = prop.Claim,
                Evidence = MapEdgeEvidence(prop.Evidence),
                LlmReasoning = prop.Reasoning,
                ContentHashAtCreation = $"{fromHash}|{toHash}",
                Status = EnrichmentStatus.Active,
                AppliedAt = DateTime.UtcNow,
                AgentVersion = AgentVersion,
                ModelId = _settings.HolocronModel,
            };
            edgeInserts.Add(doc);
            events.Add(
                new HolocronEvent
                {
                    EventType = HolocronEventType.EdgeEnrichmentCreated,
                    EnrichmentId = doc.Id,
                    FromId = doc.FromId,
                    ToId = doc.ToId,
                    Label = doc.Label,
                    Summary = $"Holocron {operation.ToString().ToLowerInvariant()} edge `{doc.Label}` from {prop.FromId} to {prop.ToId}: {Truncate(doc.Claim, 200)}",
                    TriggeredBy = triggeredBy,
                    AgentVersion = AgentVersion,
                }
            );
        }

        if (nodeInserts.Count > 0)
            await _enrichments.InsertManyAsync(nodeInserts, cancellationToken: ct);
        if (edgeInserts.Count > 0)
            await _edgeEnrichments.InsertManyAsync(edgeInserts, cancellationToken: ct);
        if (events.Count > 0)
            await _events.InsertManyAsync(events, cancellationToken: ct);

        return (nodeInserts.Count + edgeInserts.Count, evidenceFailures);
    }

    static bool ValidateNodeEvidence(List<NodeProposalEvidence> evidence, HashSet<int> validPageIds, HashSet<string> validChunkIds) =>
        ValidateEvidence(evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds);

    static bool ValidateEdgeEvidence(List<EdgeProposalEvidence> evidence, HashSet<int> validPageIds, HashSet<string> validChunkIds) =>
        ValidateEvidence(evidence?.Select(e => (e.SourcePageId, e.ChunkId)), validPageIds, validChunkIds);

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

    static bool IsNodePropertyProposalValid(GraphNode node, HolocronNodeProposal prop)
    {
        if (string.IsNullOrWhiteSpace(prop.FieldPath))
            return false;
        if (prop.Values is null || prop.Values.Count == 0 || prop.Values.All(string.IsNullOrWhiteSpace))
            return false;

        // Strip "properties." prefix if present — agent may emit either form.
        var key = prop.FieldPath.StartsWith("properties.", StringComparison.OrdinalIgnoreCase) ? prop.FieldPath["properties.".Length..] : prop.FieldPath;

        var hasExisting = node.Properties.TryGetValue(key, out var existing) && existing is not null && existing.Count > 0;
        var op = ParseOperation(prop.Operation);

        return op switch
        {
            EnrichmentOperation.Add => !hasExisting,
            EnrichmentOperation.Augment => hasExisting && prop.Values.Any(v => !existing!.Contains(v, StringComparer.OrdinalIgnoreCase)),
            _ => false, // FillGap is for edges only in v1
        };
    }

    bool IsEdgeProposalValid(
        HolocronContext context,
        HolocronEdgeProposal prop,
        HashSet<string> existingEdgeKeys,
        HashSet<string> existingNodePairs,
        HashSet<string> enrichmentEdgeKeys,
        HashSet<string> enrichmentNodePairs
    )
    {
        if (prop.FromId <= 0 || prop.ToId <= 0 || string.IsNullOrWhiteSpace(prop.Label))
            return false;

        // Label must be in the canonical registry — no inventing synonyms. The agent's
        // prompt teaches this rule but the validator is the load-bearing safety net.
        // FillGap targets an existing edge so its label is already canonical by construction;
        // we still re-check Add proposals which are the riskier path.
        if (!_knownLabels.Contains(prop.Label))
        {
            _logger.LogInformation(
                "HolocronAgent: rejecting edge proposal — label `{Label}` is not in the canonical registry. Proposed: {FromId} -> {ToId} ({Operation})",
                prop.Label,
                prop.FromId,
                prop.ToId,
                prop.Operation
            );
            return false;
        }

        // The enriched edge must touch the node we're enhancing — otherwise the agent
        // is overstepping (it should only enhance the node it was asked to).
        if (prop.FromId != context.Node.PageId && prop.ToId != context.Node.PageId)
            return false;

        var key = EdgeKey(prop.FromId, prop.ToId, prop.Label);
        var pair = NodePairKey(prop.FromId, prop.ToId);
        var op = ParseOperation(prop.Operation);

        return op switch
        {
            // Add: NO edge may already exist between this pair of nodes — regardless of
            // label or direction. Add is the LAST RESORT — when the relationship genuinely
            // doesn't exist yet. For relationships that DO exist but are under-specified,
            // the agent should use Annotate (richer context) or FillGap (temporal bounds).
            EnrichmentOperation.Add => !existingNodePairs.Contains(pair) && !enrichmentNodePairs.Contains(pair),
            // FillGap: edge MUST exist with this exact label, and at least one targeted
            // bound must currently be null. Same-label requirement here is intentional —
            // FillGap targets a specific existing edge.
            EnrichmentOperation.FillGap => existingEdgeKeys.Contains(key)
                && (prop.FromYear.HasValue || prop.ToYear.HasValue)
                && context
                    .OutgoingEdges.Concat(context.IncomingEdges)
                    .Where(e => string.Equals(e.Label, prop.Label, StringComparison.OrdinalIgnoreCase) && e.FromId == prop.FromId && e.ToId == prop.ToId)
                    .Any(e => (prop.FromYear.HasValue && !e.FromYear.HasValue) || (prop.ToYear.HasValue && !e.ToYear.HasValue)),
            // Annotate: edge MUST exist with this exact label, and at least one of
            // role / qualifier / description must be populated with non-empty content.
            // The annotation supplements the existing edge with narrative context — role
            // (e.g. "Jedi General"), qualifier (e.g. "during the Clone Wars"), description
            // (longer narrative). The merged read view exposes these alongside the edge
            // without creating a parallel line in the graph viewer.
            EnrichmentOperation.Annotate => existingEdgeKeys.Contains(key)
                && (!string.IsNullOrWhiteSpace(prop.Role) || !string.IsNullOrWhiteSpace(prop.Qualifier) || !string.IsNullOrWhiteSpace(prop.Description)),
            _ => false, // Augment is for node properties only
        };
    }

    static string EdgeKey(int from, int to, string label) => $"{from}-{to}-{label.ToLowerInvariant()}";

    /// <summary>
    /// Direction-agnostic node-pair key: <c>min(a,b)-max(a,b)</c>. Two edges with the
    /// same pair of endpoints (in either direction) collapse to the same key. Used by
    /// the Add-edge pre-flight to reject any edge between an already-connected pair.
    /// </summary>
    static string NodePairKey(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

    static EnrichmentOperation? ParseOperation(string op) => Enum.TryParse<EnrichmentOperation>(op, ignoreCase: true, out var v) ? v : null;

    static List<EnrichmentEvidence> MapNodeEvidence(List<NodeProposalEvidence> evidence) => evidence.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList();

    static List<EnrichmentEvidence> MapEdgeEvidence(List<EdgeProposalEvidence> evidence) => evidence.Select(e => BuildEvidence(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList();

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

    /// <summary>Top-level structured-output target — the LLM emits exactly this shape.</summary>
    public sealed record HolocronProposalsBatch(
        [property: JsonPropertyName("nodeProposals")] List<HolocronNodeProposal> NodeProposals,
        [property: JsonPropertyName("edgeProposals")] List<HolocronEdgeProposal> EdgeProposals
    );

    public sealed record HolocronNodeProposal(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("fieldPath")] string FieldPath,
        [property: JsonPropertyName("values")] List<string> Values,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<NodeProposalEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    /// <summary>
    /// Edge proposal — fields are populated based on the <c>operation</c>:
    /// <list type="bullet">
    ///   <item><c>Add</c>      — fromId, toId, label, optionally weight + fromYear/toYear</item>
    ///   <item><c>FillGap</c>  — fromId, toId, label match an existing edge; fromYear and/or toYear filled</item>
    ///   <item><c>Annotate</c> — fromId, toId, label match an existing edge; role / qualifier / description populated</item>
    /// </list>
    /// </summary>
    public sealed record HolocronEdgeProposal(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("fromId")] int FromId,
        [property: JsonPropertyName("toId")] int ToId,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("fromYear")] int? FromYear,
        [property: JsonPropertyName("toYear")] int? ToYear,
        [property: JsonPropertyName("weight")] double? Weight,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("qualifier")] string? Qualifier,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("evidence")] List<EdgeProposalEvidence> Evidence,
        [property: JsonPropertyName("reasoning")] string Reasoning
    );

    // Two distinct evidence record types so the JSON schema generator inlines each
    // copy rather than emitting a shared $ref. OpenAI's strict structured-output mode
    // rejects refs deeper than the top-level $defs, and a shared evidence type would
    // produce a $ref at properties.{node|edge}Proposals.items.properties.evidence.items
    // (depth 6, far beyond the depth-1 limit). Keeping the shapes identical preserves
    // the rest of the validation + mapping logic — see MapNodeEvidence / MapEdgeEvidence.
    public sealed record NodeProposalEvidence(
        [property: JsonPropertyName("sourcePageId")] int? SourcePageId,
        [property: JsonPropertyName("chunkId")] string? ChunkId,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("relevanceScore")] double? RelevanceScore
    );

    public sealed record EdgeProposalEvidence(
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
