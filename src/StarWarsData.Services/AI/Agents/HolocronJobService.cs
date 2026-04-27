using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents;

/// <summary>
/// CRUD + lifecycle helpers for <see cref="HolocronJob"/> and <see cref="ProcessedChunk"/>.
/// Singleton — wraps two Mongo collections with no per-request state. See Design-020 for
/// the full pipeline architecture.
///
/// All writes are done through this service so the executor pipeline doesn't reach into
/// raw collections. Indexes are ensured at construction (idempotent) so a fresh deploy
/// doesn't need a separate migration step for these new collections.
/// </summary>
public sealed class HolocronJobService
{
    private readonly IMongoCollection<HolocronJob> _jobs;
    private readonly IMongoCollection<ProcessedChunk> _processed;

    public HolocronJobService(IMongoClient client, IOptions<SettingsOptions> settings)
    {
        var db = client.GetDatabase(settings.Value.DatabaseName);
        _jobs = db.GetCollection<HolocronJob>(Collections.KgEnrichmentJobs);
        _processed = db.GetCollection<ProcessedChunk>(Collections.KgNodeProcessedChunks);

        // Idempotent — Mongo's CreateMany ignores duplicates by name.
        _jobs.Indexes.CreateMany(
            [
                new CreateIndexModel<HolocronJob>(Builders<HolocronJob>.IndexKeys.Ascending(j => j.PageId).Ascending(j => j.Status), new CreateIndexOptions { Name = "pageId_1_status_1" }),
                new CreateIndexModel<HolocronJob>(Builders<HolocronJob>.IndexKeys.Ascending(j => j.Status).Descending(j => j.CreatedAt), new CreateIndexOptions { Name = "status_1_createdAt_-1" }),
            ]
        );

        _processed.Indexes.CreateMany(
            [
                new CreateIndexModel<ProcessedChunk>(
                    Builders<ProcessedChunk>.IndexKeys.Ascending(p => p.NodeId).Ascending(p => p.ChunkId),
                    new CreateIndexOptions { Name = "node_chunk_unique", Unique = true }
                ),
                new CreateIndexModel<ProcessedChunk>(Builders<ProcessedChunk>.IndexKeys.Ascending(p => p.NodeId), new CreateIndexOptions { Name = "nodeId_1" }),
            ]
        );
    }

    /// <summary>
    /// Active job statuses — anything not yet terminal. Used to gate the "one job per
    /// node at a time" rule.
    /// </summary>
    private static readonly HolocronJobStatus[] ActiveStatuses =
    [
        HolocronJobStatus.Queued,
        HolocronJobStatus.Discovering,
        HolocronJobStatus.Bundling,
        HolocronJobStatus.Extracting,
        HolocronJobStatus.Consolidating,
        HolocronJobStatus.Applying,
    ];

    /// <summary>
    /// Returns the existing active job for this node if any, else inserts a new Queued job
    /// and returns it. Race-safe: the gate query + insert are kept tight, but if two
    /// concurrent callers slip past the gate the unique index on per-node active jobs
    /// is intentionally NOT enforced — duplicate Queued jobs are possible at the
    /// boundary. Worker logic in Phase B will treat the second runner as a no-op when
    /// it observes the first has progressed past Queued.
    /// </summary>
    public async Task<HolocronJob> EnqueueOrGetActiveAsync(int pageId, string nodeName, string triggeredBy, string agentVersion, string modelId, CancellationToken ct)
    {
        var existing = await _jobs
            .Find(Builders<HolocronJob>.Filter.Eq(j => j.PageId, pageId) & Builders<HolocronJob>.Filter.In(j => j.Status, ActiveStatuses))
            .SortByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return existing;

        var job = new HolocronJob
        {
            PageId = pageId,
            NodeName = nodeName,
            TriggeredBy = triggeredBy,
            AgentVersion = agentVersion,
            ModelId = modelId,
            Status = HolocronJobStatus.Queued,
        };
        await _jobs.InsertOneAsync(job, cancellationToken: ct);
        return job;
    }

    public Task<HolocronJob?> GetAsync(string jobId, CancellationToken ct) => _jobs.Find(j => j.Id == jobId).FirstOrDefaultAsync(ct)!;

    public Task<HolocronJob?> GetLastCompletedForNodeAsync(int pageId, CancellationToken ct) =>
        _jobs
            .Find(Builders<HolocronJob>.Filter.Eq(j => j.PageId, pageId) & Builders<HolocronJob>.Filter.Eq(j => j.Status, HolocronJobStatus.Completed))
            .SortByDescending(j => j.CompletedAt)
            .FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// Paginated job list with optional filters. Sort order: createdAt descending so the
    /// most recent jobs appear first, matching the Holocron Log convention.
    /// </summary>
    public async Task<HolocronJobsPage> ListAsync(HolocronJobQuery query, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<HolocronJob>>();
        if (query.Statuses is { Count: > 0 })
            filters.Add(Builders<HolocronJob>.Filter.In(j => j.Status, query.Statuses));
        if (query.PageId is { } pid && pid > 0)
            filters.Add(Builders<HolocronJob>.Filter.Eq(j => j.PageId, pid));
        if (query.Since is { } since)
            filters.Add(Builders<HolocronJob>.Filter.Gte(j => j.CreatedAt, since));

        var filter = filters.Count > 0 ? Builders<HolocronJob>.Filter.And(filters) : FilterDefinition<HolocronJob>.Empty;

        var pageSize = query.PageSize is > 0 and <= 100 ? query.PageSize : 20;
        var page = query.Page > 0 ? query.Page : 1;

        var totalTask = _jobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rowsTask = _jobs.Find(filter).SortByDescending(j => j.CreatedAt).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);

        await Task.WhenAll(totalTask, rowsTask);
        return new HolocronJobsPage(rowsTask.Result, (int)totalTask.Result, page, pageSize);
    }

    /// <summary>
    /// Atomic state transition. The optional <paramref name="updates"/> argument lets a
    /// caller bundle counter updates (e.g. <c>chunksDiscovered</c>) into the same write
    /// so progress and status stay consistent on the wire.
    /// </summary>
    public Task TransitionAsync(string jobId, HolocronJobStatus to, UpdateDefinition<HolocronJob>? updates, CancellationToken ct)
    {
        var ub = Builders<HolocronJob>.Update.Set(j => j.Status, to);
        if (to == HolocronJobStatus.Discovering)
            ub = ub.Set(j => j.StartedAt, DateTime.UtcNow);
        if (to is HolocronJobStatus.Completed or HolocronJobStatus.Failed)
            ub = ub.Set(j => j.CompletedAt, DateTime.UtcNow);
        if (updates is not null)
            ub = Builders<HolocronJob>.Update.Combine(ub, updates);
        return _jobs.UpdateOneAsync(j => j.Id == jobId, ub, cancellationToken: ct);
    }

    public Task FailAsync(string jobId, string error, CancellationToken ct) => TransitionAsync(jobId, HolocronJobStatus.Failed, Builders<HolocronJob>.Update.Set(j => j.Error, error), ct);

    public Task IncrementCompletedBatchesAsync(string jobId, int by, CancellationToken ct) =>
        _jobs.UpdateOneAsync(j => j.Id == jobId, Builders<HolocronJob>.Update.Inc(j => j.CompletedBatches, by), cancellationToken: ct);

    /// <summary>
    /// Hash-keyed lookup of which chunks were already processed for a given node. Used
    /// by Discovery to filter the candidate set down to "new or content-changed" chunks.
    /// Returns a dictionary keyed by chunkId mapping to the recorded contentHash.
    /// </summary>
    public async Task<Dictionary<string, string>> GetProcessedChunkHashesAsync(int nodeId, CancellationToken ct)
    {
        var rows = await _processed.Find(p => p.NodeId == nodeId).Project(p => new { p.ChunkId, p.ChunkContentHash }).ToListAsync(ct);
        return rows.ToDictionary(r => r.ChunkId, r => r.ChunkContentHash, StringComparer.Ordinal);
    }

    /// <summary>
    /// Record that a set of chunks was processed for a given node. Uses an unordered
    /// bulk write so duplicate keys (concurrent jobs racing on the same node) silently
    /// skip rather than aborting the whole batch.
    /// </summary>
    public async Task RecordProcessedAsync(IEnumerable<ProcessedChunk> records, CancellationToken ct)
    {
        var ops = records
            .Select(r =>
                (WriteModel<ProcessedChunk>)
                    new ReplaceOneModel<ProcessedChunk>(Builders<ProcessedChunk>.Filter.Eq(p => p.NodeId, r.NodeId) & Builders<ProcessedChunk>.Filter.Eq(p => p.ChunkId, r.ChunkId), r)
                    {
                        IsUpsert = true,
                    }
            )
            .ToList();

        if (ops.Count == 0)
            return;
        await _processed.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
    }
}

/// <summary>Filter parameters for <see cref="HolocronJobService.ListAsync"/>.</summary>
public sealed record HolocronJobQuery(List<HolocronJobStatus>? Statuses = null, int? PageId = null, DateTime? Since = null, int Page = 1, int PageSize = 20);

/// <summary>Paginated job list response.</summary>
public sealed record HolocronJobsPage(List<HolocronJob> Items, int Total, int Page, int PageSize);
