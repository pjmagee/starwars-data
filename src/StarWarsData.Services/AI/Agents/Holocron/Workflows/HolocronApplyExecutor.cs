using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 5 — final stage of the Holocron workflow (Design-020). Pure C#, no LLM.
/// Reads the consolidated, pre-flight-validated proposals from
/// <see cref="HolocronConsolidatorExecutor"/> and writes them to the live
/// collections in a single coordinated burst:
///
/// <list type="bullet">
///   <item><c>kg.enrichments</c> — node-property enrichments (one row per
///         <c>(pageId, fieldPath)</c> in the consolidated set).</item>
///   <item><c>kg.edge_enrichments</c> — Annotate / FillGap / Add edge
///         enrichments.</item>
///   <item><c>kg.events</c> — one <c>HolocronEvent</c> per applied enrichment
///         for the audit log + Holocron Log UI.</item>
///   <item><c>kg.node_processed_chunks</c> — every chunk that was fed to the
///         extractor (success OR failure path) gets recorded in the
///         per-(node, chunk) ledger so the next change-aware re-run skips
///         unchanged content.</item>
/// </list>
///
/// Returns a final summary string consumed by the workflow's
/// <c>WithOutputFrom(applyExecutor)</c> output, then the orchestrator parses it
/// for logging / final job-doc transition.
///
/// **Op selection** for node-proposals (Add vs Augment) is decided here, not by
/// the agent — the agent just supplies "this field should contain these values
/// with this evidence". If the field already has values, the operation is
/// <see cref="EnrichmentOperation.Augment"/> with values deduped against the
/// existing list; otherwise it's <see cref="EnrichmentOperation.Add"/>.
/// </summary>
internal sealed class HolocronApplyExecutor : Executor<string, string>
{
    public const string Scope = "HolocronApply";

    readonly IMongoClient _mongoClient;
    readonly SettingsOptions _settings;
    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly HolocronJobService _jobService;
    readonly int _pageId;
    readonly string _jobId;
    readonly string _triggeredBy;

    public HolocronApplyExecutor(
        IMongoClient mongoClient,
        SettingsOptions settings,
        ILogger logger,
        HolocronJobService jobService,
        int pageId,
        string jobId,
        string triggeredBy,
        HolocronEnhancementTracker? tracker
    )
        : base("HolocronApply")
    {
        _mongoClient = mongoClient;
        _settings = settings;
        _logger = logger;
        _jobService = jobService;
        _pageId = pageId;
        _jobId = jobId;
        _triggeredBy = triggeredBy;
        _tracker = tracker;
    }

    IMongoDatabase Db => _mongoClient.GetDatabase(_settings.DatabaseName);
    IMongoCollection<NodeEnrichment> NodeEnrichments => Db.GetCollection<NodeEnrichment>(Collections.KgEnrichments);
    IMongoCollection<EdgeEnrichment> EdgeEnrichments => Db.GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);
    IMongoCollection<HolocronEvent> Events => Db.GetCollection<HolocronEvent>(Collections.KgEvents);
    IMongoCollection<GraphNode> Nodes => Db.GetCollection<GraphNode>(Collections.KgNodes);

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var node =
            await context.ReadStateAsync<HolocronNodeSnapshot>(HolocronContextDiscoveryExecutor.KeyNode, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronApply: no node in Discovery state");
        var consolidated =
            await context.ReadStateAsync<HolocronConsolidatedProposals>(HolocronConsolidatorExecutor.KeyConsolidated, HolocronConsolidatorExecutor.Scope, ct)
            ?? new HolocronConsolidatedProposals([], [], [], [], 0, 0, 0);
        var newChunks = await context.ReadStateAsync<List<HolocronChunkRef>>(HolocronContextDiscoveryExecutor.KeyNewChunks, HolocronContextDiscoveryExecutor.Scope, ct) ?? [];

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Applying, null, ct);
        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Applying,
            $"Writing {consolidated.AnnotateEdges.Count + consolidated.FillGapEdges.Count + consolidated.AddEdges.Count + consolidated.NodeProposals.Count} enrichments + recording {newChunks.Count} processed chunks...",
            currentStep: 0,
            totalSteps: 1,
            currentItem: node.Name
        );

        // ── 1. Resolve endpoint hashes for combined-hash stamping on edges ─
        // Edge enrichments stamp `${fromHash}|${toHash}` so a Phase 1 rebuild that
        // changes either endpoint flips the enrichment to Stale via the staleness sweep.
        var endpointIds = consolidated
            .AnnotateEdges.SelectMany(p => new[] { p.FromId, p.ToId })
            .Concat(consolidated.FillGapEdges.SelectMany(p => new[] { p.FromId, p.ToId }))
            .Concat(consolidated.AddEdges.SelectMany(p => new[] { p.FromId, p.ToId }))
            .Distinct()
            .Where(id => id != _pageId)
            .ToList();
        var hashLookup =
            endpointIds.Count == 0
                ? new Dictionary<int, string?>()
                : (await Nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, endpointIds)).Project(n => new { n.PageId, n.ContentHash }).ToListAsync(ct)).ToDictionary(
                    x => x.PageId,
                    x => x.ContentHash
                );
        // Self always known — pulled from the snapshot.
        hashLookup[_pageId] = node.ContentHash;

        var nodeInserts = new List<NodeEnrichment>();
        var edgeInserts = new List<EdgeEnrichment>();
        var events = new List<HolocronEvent>();

        // ── 2. Node proposals ──────────────────────────────────────────────
        // Op decision: Add vs Augment based on whether the field already has values
        // in the canonical infobox-derived properties dict. Same rule as
        // HolocronAgent.ApplyProposalsAsync — the agent never picks an op directly.
        foreach (var p in consolidated.NodeProposals)
        {
            var key = p.FieldPath;
            var hasExisting = node.Properties.TryGetValue(key, out var existing) && existing is not null && existing.Count > 0;

            EnrichmentOperation op;
            List<string> finalValues;
            if (hasExisting)
            {
                finalValues = p.Values.Where(v => !string.IsNullOrWhiteSpace(v) && !existing!.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
                if (finalValues.Count == 0)
                    continue; // every proposed value already present — silent dedup
                op = EnrichmentOperation.Augment;
            }
            else
            {
                finalValues = p.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (finalValues.Count == 0)
                    continue;
                op = EnrichmentOperation.Add;
            }

            var doc = new NodeEnrichment
            {
                PageId = _pageId,
                FieldPath = key,
                Operation = op,
                Value = ToBsonValue(finalValues),
                Claim = p.Claim,
                Evidence = p.Evidence.Select(e => BuildEvidence(e)).ToList(),
                LlmReasoning = p.Reasoning,
                ContentHashAtCreation = node.ContentHash,
                Status = EnrichmentStatus.Active,
                AppliedAt = DateTime.UtcNow,
                AgentVersion = HolocronAgent.AgentVersion,
                ModelId = _settings.HolocronModel,
                JobId = _jobId,
            };
            nodeInserts.Add(doc);
            events.Add(
                new HolocronEvent
                {
                    EventType = HolocronEventType.EnrichmentCreated,
                    EnrichmentId = doc.Id,
                    PageId = doc.PageId,
                    FieldPath = doc.FieldPath,
                    Summary = $"Holocron {op.ToString().ToLowerInvariant()} `{doc.FieldPath}` on {node.Name}: {Truncate(doc.Claim, 200)}",
                    TriggeredBy = _triggeredBy,
                    AgentVersion = HolocronAgent.AgentVersion,
                    JobId = _jobId,
                }
            );
        }

        // ── 3. Annotate edge proposals ─────────────────────────────────────
        foreach (var p in consolidated.AnnotateEdges)
        {
            var (fromHash, toHash) = ResolveHashes(hashLookup, p.FromId, p.ToId);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (!string.IsNullOrWhiteSpace(p.Role))
                value["role"] = p.Role.Trim();
            if (!string.IsNullOrWhiteSpace(p.Qualifier))
                value["qualifier"] = p.Qualifier.Trim();
            if (!string.IsNullOrWhiteSpace(p.Description))
                value["description"] = p.Description.Trim();

            var doc = BuildEdgeEnrichment(p.FromId, p.ToId, p.Label, EnrichmentOperation.Annotate, value, p.Claim, p.Evidence.Select(BuildEvidence).ToList(), p.Reasoning, fromHash, toHash);
            edgeInserts.Add(doc);
            events.Add(BuildEdgeEvent(doc, EnrichmentOperation.Annotate));
        }

        // ── 4. FillGap edge proposals ──────────────────────────────────────
        foreach (var p in consolidated.FillGapEdges)
        {
            var (fromHash, toHash) = ResolveHashes(hashLookup, p.FromId, p.ToId);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (p.FromYear.HasValue)
                value["fromYear"] = p.FromYear.Value;
            if (p.ToYear.HasValue)
                value["toYear"] = p.ToYear.Value;

            var doc = BuildEdgeEnrichment(p.FromId, p.ToId, p.Label, EnrichmentOperation.FillGap, value, p.Claim, p.Evidence.Select(BuildEvidence).ToList(), p.Reasoning, fromHash, toHash);
            edgeInserts.Add(doc);
            events.Add(BuildEdgeEvent(doc, EnrichmentOperation.FillGap));
        }

        // ── 5. Add edge proposals ──────────────────────────────────────────
        foreach (var p in consolidated.AddEdges)
        {
            var (fromHash, toHash) = ResolveHashes(hashLookup, p.FromId, p.ToId);
            if (fromHash is null || toHash is null)
                continue;

            var value = new BsonDocument();
            if (p.FromYear.HasValue)
                value["fromYear"] = p.FromYear.Value;
            if (p.ToYear.HasValue)
                value["toYear"] = p.ToYear.Value;
            if (p.Weight.HasValue)
                value["weight"] = p.Weight.Value;

            var doc = BuildEdgeEnrichment(p.FromId, p.ToId, p.Label, EnrichmentOperation.Add, value, p.Claim, p.Evidence.Select(BuildEvidence).ToList(), p.Reasoning, fromHash, toHash);
            edgeInserts.Add(doc);
            events.Add(BuildEdgeEvent(doc, EnrichmentOperation.Add));
        }

        // ── 6. Persist ─────────────────────────────────────────────────────
        // BulkWrite with IsOrdered=false so a duplicate-key error from one doc
        // doesn't abort the rest of the batch. Migration 0014 creates partial
        // unique indexes on (jobId, identity-fields) for all three collections,
        // so a re-run of the same Apply (after a crash between InsertMany and
        // the Completed transition) is a silent no-op via E11000 — see Design-020
        // "Apply isn't strictly idempotent" risk.
        var nodeDupes = await BulkInsertTolerantAsync(NodeEnrichments, nodeInserts, "kg.enrichments", ct);
        var edgeDupes = await BulkInsertTolerantAsync(EdgeEnrichments, edgeInserts, "kg.edge_enrichments", ct);
        var eventDupes = await BulkInsertTolerantAsync(Events, events, "kg.events", ct);

        if (nodeDupes + edgeDupes + eventDupes > 0)
        {
            _logger.LogInformation(
                "HolocronApply: JobId={JobId} — replay-safe path absorbed {NodeDupes} node + {EdgeDupes} edge + {EventDupes} event duplicate-key writes (prior partial-Apply detected).",
                _jobId,
                nodeDupes,
                edgeDupes,
                eventDupes
            );
        }

        // ── 7. Record processed chunks (the ledger that drives skip-if-unchanged) ──
        // Every chunk fed to the extractor — success OR fail path — gets recorded
        // so the next run knows we've already looked at it. Otherwise a chunk that
        // happened to produce zero proposals would be re-processed forever.
        var processedRecords = newChunks.Select(c => new ProcessedChunk
        {
            NodeId = _pageId,
            ChunkId = c.ChunkId,
            ChunkContentHash = c.ContentHash,
            ProcessedAt = DateTime.UtcNow,
            JobId = _jobId,
            AgentVersion = HolocronAgent.AgentVersion,
        });
        await _jobService.RecordProcessedAsync(processedRecords, ct);

        // ── 8. Final job-doc transition + event ────────────────────────────
        var enrichmentsApplied = nodeInserts.Count + edgeInserts.Count;
        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Completed, Builders<HolocronJob>.Update.Set(j => j.EnrichmentsApplied, enrichmentsApplied), ct);

        await context.AddEventAsync(new HolocronApplyCompleteEvent(new HolocronApplyCompleteData(nodeInserts.Count, edgeInserts.Count, events.Count, newChunks.Count)), ct);

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Completed,
            $"Done — applied {enrichmentsApplied} enrichments, recorded {newChunks.Count} processed chunks.",
            currentStep: 1,
            totalSteps: 1,
            enrichmentsApplied: enrichmentsApplied
        );

        _logger.LogInformation(
            "HolocronApply: PageId={PageId} ({Name}) — wrote {Nodes} node + {Edges} edge enrichments, {Events} events, {Chunks} processed-chunk records.",
            _pageId,
            node.Name,
            nodeInserts.Count,
            edgeInserts.Count,
            events.Count,
            newChunks.Count
        );

        return $"Applied {enrichmentsApplied} enrichments for {node.Name}";
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    static (string? FromHash, string? ToHash) ResolveHashes(Dictionary<int, string?> hashLookup, int fromId, int toId) => (hashLookup.GetValueOrDefault(fromId), hashLookup.GetValueOrDefault(toId));

    /// <summary>
    /// Insert <paramref name="docs"/> using an unordered bulk write that swallows
    /// duplicate-key errors (E11000) silently. This is the load-bearing replay safety
    /// net for the Apply executor: if the process dies after a partial Apply but before
    /// the workflow's Completed transition, the resumed run re-enters Apply and
    /// re-attempts the same writes — the partial unique indexes from migration 0014
    /// reject the duplicates and we count them rather than failing the run.
    ///
    /// Returns the number of duplicates absorbed. Any non-E11000 write errors abort
    /// the run by re-throwing the original exception.
    /// </summary>
    async Task<int> BulkInsertTolerantAsync<T>(IMongoCollection<T> collection, IReadOnlyList<T> docs, string collectionName, CancellationToken ct)
    {
        if (docs.Count == 0)
            return 0;

        var ops = docs.Select(d => (WriteModel<T>)new InsertOneModel<T>(d)).ToList();
        try
        {
            await collection.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
            return 0;
        }
        catch (MongoBulkWriteException<T> ex)
        {
            // E11000 = duplicate key. Anything else is a real error.
            const int duplicateKeyCode = 11000;
            var nonDupErrors = ex.WriteErrors.Where(e => e.Code != duplicateKeyCode).ToList();
            if (nonDupErrors.Count > 0)
            {
                _logger.LogError("HolocronApply: BulkWrite to {Collection} produced {Count} non-duplicate-key errors; aborting Apply.", collectionName, nonDupErrors.Count);
                throw;
            }
            return ex.WriteErrors.Count;
        }
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
            AgentVersion = HolocronAgent.AgentVersion,
            ModelId = _settings.HolocronModel,
            JobId = _jobId,
        };

    HolocronEvent BuildEdgeEvent(EdgeEnrichment doc, EnrichmentOperation op) =>
        new()
        {
            EventType = HolocronEventType.EdgeEnrichmentCreated,
            EnrichmentId = doc.Id,
            FromId = doc.FromId,
            ToId = doc.ToId,
            Label = doc.Label,
            Summary = $"Holocron {op.ToString().ToLowerInvariant()} edge `{doc.Label}` from {doc.FromId} to {doc.ToId}: {Truncate(doc.Claim, 200)}",
            TriggeredBy = _triggeredBy,
            AgentVersion = HolocronAgent.AgentVersion,
            JobId = _jobId,
        };

    static EnrichmentEvidence BuildEvidence(HolocronEvidencePayload p) =>
        new()
        {
            SourcePageId = p.SourcePageId ?? 0,
            ChunkId = p.ChunkId,
            Excerpt = string.IsNullOrEmpty(p.Excerpt) ? string.Empty : (p.Excerpt.Length > 1000 ? p.Excerpt[..1000] : p.Excerpt),
            RelevanceScore = p.RelevanceScore,
        };

    static BsonValue ToBsonValue(List<string> values) => values.Count == 1 ? new BsonString(values[0]) : new BsonArray(values);

    static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
