using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 3 of the Holocron workflow (Design-020). One LLM call per batch using
/// the same four-array structured-output schema the synchronous Holocron path
/// uses. Accumulates raw proposals across batches in a single
/// <see cref="HolocronRawProposalSet"/> for the consolidator to dedupe.
///
/// Resilience strategy mirrors Timeline's <c>BatchExtractionExecutor</c>:
///
/// <list type="bullet">
///   <item><b>Workflow checkpoint</b> — between supersteps, the framework persists
///         the in-memory state (processed batch indices + accumulated proposals)
///         via <see cref="OnCheckpointingAsync"/>. A resumed run re-enters
///         <see cref="HandleAsync"/> and skips already-processed batches.</item>
///   <item><b>Per-batch Mongo progress</b> — saved to <c>genai.holocron_progress</c>
///         after EACH batch. Survives an in-flight process kill (the workflow
///         checkpoint only saves at superstep boundaries; this collection saves
///         after every individual LLM call so a crash mid-batch doesn't replay
///         already-completed batches).</item>
/// </list>
///
/// Failures in a single batch don't break the run — the batch is marked
/// processed (so we don't loop) and a failure event is surfaced for the
/// activity log. The remaining batches still execute.
/// </summary>
internal sealed class HolocronProposalExtractorExecutor : Executor<string, string>
{
    public const string Scope = "HolocronExtraction";
    public const string KeyCheckpoint = "checkpoint";
    public const string KeyRawProposals = "rawProposals";

    readonly HolocronAgent _agent;
    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly HolocronJobService _jobService;
    readonly IMongoCollection<HolocronExtractionProgressDoc> _progressCollection;
    readonly IMongoCollection<ArticleChunk> _chunks;
    readonly int _pageId;
    readonly string _jobId;

    HashSet<int> _processedBatchIndices = [];
    HolocronRawProposalSet _accumulated = new(AnnotateEdges: [], FillGapEdges: [], AddEdges: [], NodeProposals: []);

    public HolocronProposalExtractorExecutor(
        HolocronAgent agent,
        ILogger logger,
        HolocronJobService jobService,
        IMongoClient mongoClient,
        string databaseName,
        int pageId,
        string jobId,
        HolocronEnhancementTracker? tracker
    )
        : base("HolocronProposalExtractor")
    {
        _agent = agent;
        _logger = logger;
        _tracker = tracker;
        _jobService = jobService;
        _pageId = pageId;
        _jobId = jobId;
        var db = mongoClient.GetDatabase(databaseName);
        _progressCollection = db.GetCollection<HolocronExtractionProgressDoc>(Collections.GenaiHolocronProgress);
        _chunks = db.GetCollection<ArticleChunk>(Collections.SearchChunks);
    }

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        return context.QueueStateUpdateAsync(KeyCheckpoint, new HolocronExtractionCheckpoint(_processedBatchIndices.ToList(), _accumulated), Scope, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        var checkpoint = await context.ReadStateAsync<HolocronExtractionCheckpoint>(KeyCheckpoint, Scope, cancellationToken);
        if (checkpoint is not null)
        {
            _processedBatchIndices = [.. checkpoint.ProcessedBatchIndices];
            _accumulated = checkpoint.Proposals;
            _logger.LogInformation(
                "HolocronExtraction restored: {Processed} batches, {Annotates} annotates, {FillGaps} fillGaps, {Adds} adds, {NodeProps} nodeProps",
                _processedBatchIndices.Count,
                _accumulated.AnnotateEdges.Count,
                _accumulated.FillGapEdges.Count,
                _accumulated.AddEdges.Count,
                _accumulated.NodeProposals.Count
            );
        }
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var node =
            await context.ReadStateAsync<HolocronNodeSnapshot>(HolocronContextDiscoveryExecutor.KeyNode, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no node in Discovery state");
        var outEdges =
            await context.ReadStateAsync<List<RelationshipEdge>>(HolocronContextDiscoveryExecutor.KeyOutEdges, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no outEdges in Discovery state");
        var inEdges =
            await context.ReadStateAsync<List<RelationshipEdge>>(HolocronContextDiscoveryExecutor.KeyInEdges, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no inEdges in Discovery state");
        var neighbours =
            await context.ReadStateAsync<List<HolocronNeighbour>>(HolocronContextDiscoveryExecutor.KeyNeighbours, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no neighbours in Discovery state");
        var canonicalLabels =
            await context.ReadStateAsync<List<HolocronCanonicalLabel>>(HolocronContextDiscoveryExecutor.KeyCanonicalLabels, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no canonicalLabels in Discovery state");
        var ownPageChunks = await context.ReadStateAsync<List<HolocronChunkPayload>>(HolocronContextDiscoveryExecutor.KeyOwnPageChunks, HolocronContextDiscoveryExecutor.Scope, ct) ?? [];
        var batches =
            await context.ReadStateAsync<List<HolocronBatch>>(HolocronBundlerExecutor.KeyBatches, HolocronBundlerExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronExtractor: no batches in Bundler state");

        // Restore per-batch progress from MongoDB (survives mid-extraction process kills).
        await RestoreFromMongoProgressAsync(ct);

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Extracting, null, ct);

        var remaining = batches.Where(b => !_processedBatchIndices.Contains(b.BatchIndex)).ToList();

        if (batches.Count == 0)
        {
            _logger.LogInformation("HolocronExtractor: PageId={PageId} ({Name}) — no batches to extract.", _pageId, node.Name);
            await context.QueueStateUpdateAsync(KeyRawProposals, _accumulated, Scope, ct);
            await ClearMongoProgressAsync(ct);
            return $"No batches for {node.Name}";
        }

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Extracting,
            $"Extracting from {remaining.Count} batches ({_processedBatchIndices.Count} already done)...",
            currentStep: _processedBatchIndices.Count,
            totalSteps: batches.Count,
            currentItem: node.Name
        );

        foreach (var batch in remaining)
        {
            ct.ThrowIfCancellationRequested();

            var pageTitles = batch.Chunks.Select(c => c.Title).Distinct().ToList();
            await context.AddEventAsync(new HolocronBatchExtractionStartedEvent(new HolocronBatchExtractionStartedData(batch.BatchIndex + 1, batches.Count, batch.Chunks.Count, pageTitles)), ct);

            _tracker?.UpdateProgress(
                _pageId,
                HolocronJobStatus.Extracting,
                $"Extracting batch {batch.BatchIndex + 1}/{batches.Count} ({batch.Chunks.Count} chunks across {pageTitles.Count} pages)",
                currentStep: _processedBatchIndices.Count,
                totalSteps: batches.Count,
                currentItem: $"Batch {batch.BatchIndex + 1}: {pageTitles.Count} pages",
                proposalsExtracted: _accumulated.AnnotateEdges.Count + _accumulated.FillGapEdges.Count + _accumulated.AddEdges.Count + _accumulated.NodeProposals.Count
            );

            try
            {
                // Rehydrate the text bodies for THIS batch only — workflow state carries
                // refs (no text) to keep the framework checkpoint under Mongo's 16 MB doc
                // limit. Bulk find by indexed _id is microseconds even for 50+ chunks.
                var batchIds = batch.Chunks.Select(c => c.ChunkId).ToList();
                var fetched = await _chunks.Find(Builders<ArticleChunk>.Filter.In(c => c.Id, batchIds)).Project(c => new { c.Id, c.Text }).ToListAsync(ct);
                var textById = fetched.ToDictionary(f => f.Id, f => f.Text ?? string.Empty, StringComparer.Ordinal);
                var batchPayloads = batch
                    .Chunks.Select(r => new HolocronChunkPayload(r.ChunkId, r.PageId, r.Title, r.Heading, r.Section, textById.GetValueOrDefault(r.ChunkId, string.Empty), r.ContentHash))
                    .ToList();

                var proposalsBatch = await _agent.CallLlmForBatchAsync(node, outEdges, inEdges, neighbours, canonicalLabels, ownPageChunks, batchPayloads, ct);

                if (proposalsBatch is null)
                {
                    await context.AddEventAsync(new HolocronBatchExtractionEmptyEvent(new HolocronBatchExtractionEmptyData(batch.BatchIndex + 1, batch.Chunks.Count)), ct);
                }
                else
                {
                    var annotates = proposalsBatch
                        .AnnotateEdges.Select(p => new HolocronAnnotateProposal(
                            batch.BatchIndex,
                            p.FromId,
                            p.ToId,
                            p.Label,
                            p.Role,
                            p.Qualifier,
                            p.Description,
                            p.Claim,
                            p.Evidence?.Select(e => new HolocronEvidencePayload(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList() ?? [],
                            p.Reasoning
                        ))
                        .ToList();
                    var fillGaps = proposalsBatch
                        .FillGapEdges.Select(p => new HolocronFillGapProposal(
                            batch.BatchIndex,
                            p.FromId,
                            p.ToId,
                            p.Label,
                            p.FromYear,
                            p.ToYear,
                            p.Claim,
                            p.Evidence?.Select(e => new HolocronEvidencePayload(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList() ?? [],
                            p.Reasoning
                        ))
                        .ToList();
                    var addEdges = proposalsBatch
                        .AddEdges.Select(p => new HolocronAddEdgeProposal(
                            batch.BatchIndex,
                            p.FromId,
                            p.ToId,
                            p.Label,
                            p.FromYear,
                            p.ToYear,
                            p.Weight,
                            p.Claim,
                            p.Evidence?.Select(e => new HolocronEvidencePayload(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList() ?? [],
                            p.Reasoning
                        ))
                        .ToList();
                    var nodeProps = proposalsBatch
                        .NodeProposals.Select(p => new HolocronNodeProposalPayload(
                            batch.BatchIndex,
                            p.FieldPath,
                            p.Values ?? [],
                            p.Claim,
                            p.Evidence?.Select(e => new HolocronEvidencePayload(e.SourcePageId, e.ChunkId, e.Excerpt, e.RelevanceScore)).ToList() ?? [],
                            p.Reasoning
                        ))
                        .ToList();

                    _accumulated = new HolocronRawProposalSet(
                        AnnotateEdges: [.. _accumulated.AnnotateEdges, .. annotates],
                        FillGapEdges: [.. _accumulated.FillGapEdges, .. fillGaps],
                        AddEdges: [.. _accumulated.AddEdges, .. addEdges],
                        NodeProposals: [.. _accumulated.NodeProposals, .. nodeProps]
                    );

                    var batchTotal = annotates.Count + fillGaps.Count + addEdges.Count + nodeProps.Count;
                    if (batchTotal == 0)
                    {
                        await context.AddEventAsync(new HolocronBatchExtractionEmptyEvent(new HolocronBatchExtractionEmptyData(batch.BatchIndex + 1, batch.Chunks.Count)), ct);
                    }
                    else
                    {
                        await context.AddEventAsync(
                            new HolocronProposalsExtractedEvent(new HolocronProposalsExtractedData(batch.BatchIndex + 1, annotates.Count, fillGaps.Count, addEdges.Count, nodeProps.Count)),
                            ct
                        );
                    }

                    _logger.LogInformation(
                        "HolocronExtractor: batch {Idx}/{Total} → annotate={A} fillGap={F} add={Ad} nodeProp={N}",
                        batch.BatchIndex + 1,
                        batches.Count,
                        annotates.Count,
                        fillGaps.Count,
                        addEdges.Count,
                        nodeProps.Count
                    );
                }

                _processedBatchIndices.Add(batch.BatchIndex);
                await SaveMongoProgressAsync(ct);
                await _jobService.IncrementCompletedBatchesAsync(_jobId, 1, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HolocronExtractor: batch {Idx} failed; marking processed and continuing.", batch.BatchIndex + 1);
                _processedBatchIndices.Add(batch.BatchIndex);
                await SaveMongoProgressAsync(ct);
                await _jobService.IncrementCompletedBatchesAsync(_jobId, 1, ct);
                await context.AddEventAsync(new HolocronBatchExtractionFailedEvent(new HolocronBatchExtractionFailedData(batch.BatchIndex + 1, batch.Chunks.Count, ex.Message)), ct);
            }
        }

        await context.QueueStateUpdateAsync(KeyRawProposals, _accumulated, Scope, ct);
        await ClearMongoProgressAsync(ct);

        var totalProposals = _accumulated.AnnotateEdges.Count + _accumulated.FillGapEdges.Count + _accumulated.AddEdges.Count + _accumulated.NodeProposals.Count;
        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Extracting, Builders<HolocronJob>.Update.Set(j => j.ProposalsExtracted, totalProposals), ct);

        _logger.LogInformation(
            "HolocronExtractor: PageId={PageId} ({Name}) complete — {BatchCount} batches → {Annotates}A {FillGaps}F {Adds}E {NodeProps}N raw proposals.",
            _pageId,
            node.Name,
            batches.Count,
            _accumulated.AnnotateEdges.Count,
            _accumulated.FillGapEdges.Count,
            _accumulated.AddEdges.Count,
            _accumulated.NodeProposals.Count
        );

        return $"Extracted {totalProposals} raw proposals from {batches.Count} batches for {node.Name}";
    }

    // ── MongoDB per-batch progress persistence ─────────────────────────────

    async Task RestoreFromMongoProgressAsync(CancellationToken ct)
    {
        var doc = await _progressCollection.Find(Builders<HolocronExtractionProgressDoc>.Filter.Eq(d => d.Id, _pageId)).FirstOrDefaultAsync(ct);
        if (doc is not null && doc.ProcessedBatchIndices.Count > _processedBatchIndices.Count)
        {
            _processedBatchIndices = [.. doc.ProcessedBatchIndices];
            _accumulated = new HolocronRawProposalSet(doc.AnnotateEdges, doc.FillGapEdges, doc.AddEdges, doc.NodeProposals);
            _logger.LogInformation("HolocronExtractor: restored Mongo progress — {Processed} batches.", _processedBatchIndices.Count);
        }
    }

    async Task SaveMongoProgressAsync(CancellationToken ct)
    {
        var doc = new HolocronExtractionProgressDoc
        {
            Id = _pageId,
            JobId = _jobId,
            ProcessedBatchIndices = _processedBatchIndices.ToList(),
            AnnotateEdges = _accumulated.AnnotateEdges,
            FillGapEdges = _accumulated.FillGapEdges,
            AddEdges = _accumulated.AddEdges,
            NodeProposals = _accumulated.NodeProposals,
            UpdatedAt = DateTime.UtcNow,
        };
        await _progressCollection.ReplaceOneAsync(Builders<HolocronExtractionProgressDoc>.Filter.Eq(d => d.Id, _pageId), doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    async Task ClearMongoProgressAsync(CancellationToken ct)
    {
        await _progressCollection.DeleteOneAsync(Builders<HolocronExtractionProgressDoc>.Filter.Eq(d => d.Id, _pageId), ct);
    }
}

/// <summary>
/// MongoDB document for per-batch extraction progress. Saved after every batch
/// so a process kill mid-loop doesn't replay completed batches. Keyed by
/// <c>pageId</c> — one node has at most one in-flight extraction per
/// Design-020's per-page invariant.
/// </summary>
internal sealed class HolocronExtractionProgressDoc
{
    [BsonId]
    public int Id { get; set; } // = pageId

    public string JobId { get; set; } = string.Empty;
    public List<int> ProcessedBatchIndices { get; set; } = [];
    public List<HolocronAnnotateProposal> AnnotateEdges { get; set; } = [];
    public List<HolocronFillGapProposal> FillGapEdges { get; set; } = [];
    public List<HolocronAddEdgeProposal> AddEdges { get; set; } = [];
    public List<HolocronNodeProposalPayload> NodeProposals { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}
