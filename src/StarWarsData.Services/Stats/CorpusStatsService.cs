using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Stats;

namespace StarWarsData.Services;

/// <summary>
/// Design-037 / ADR-009: cheap, cached, public corpus-health figures. Owns its own
/// thin Mongo access (no PageDownloader / ETL dependency) so the public path stays
/// off admin/ETL surface. Collection totals use <c>EstimatedDocumentCount</c>
/// (O(1) metadata, never scans). The whole snapshot is cached in
/// <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> so anonymous traffic can't
/// hammer the shared mongod — at most one refresh per TTL per ApiService instance.
/// Browse methods (Phase 2) are uncached but bounded and indexed.
/// </summary>
public sealed class CorpusStatsService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, IMemoryCache cache, ILogger<CorpusStatsService> logger)
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    const string CacheKey = "corpus-stats-snapshot";

    // Mirrors PageDownloader.IncrementalSyncJobName (private there); the daily wiki
    // sync writes raw.job_state with this _id.
    const string IncrementalSyncJobName = "IncrementalSync";

    const int SnapshotRecentCount = 25; // recent lists are sliced to the caller's limit (≤ this)
    const int MaxBrowseTake = 100;

    IMongoDatabase Db => mongoClient.GetDatabase(settings.Value.DatabaseName);

    public sealed record Snapshot(CorpusStatsDto Corpus, IReadOnlyList<RecentArticleDto> RecentPages, IReadOnlyList<RecentChunkDto> RecentChunks);

    public Task<Snapshot> GetSnapshotAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(
            CacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await BuildSnapshotAsync(ct);
            }
        )!;

    async Task<Snapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var db = Db;

        var kgNodes = db.GetCollection<BsonDocument>(Collections.KgNodes).EstimatedDocumentCountAsync(cancellationToken: ct);
        var kgEdges = db.GetCollection<BsonDocument>(Collections.KgEdges).EstimatedDocumentCountAsync(cancellationToken: ct);
        var pages = db.GetCollection<BsonDocument>(Collections.Pages).EstimatedDocumentCountAsync(cancellationToken: ct);
        var chunks = db.GetCollection<BsonDocument>(Collections.SearchChunks).EstimatedDocumentCountAsync(cancellationToken: ct);
        await Task.WhenAll(kgNodes, kgEdges, pages, chunks);

        var lastSync = await db.GetCollection<JobState>(Collections.JobState).Find(j => j.JobName == IncrementalSyncJobName).Project(j => (DateTime?)j.UpdatedAt).FirstOrDefaultAsync(ct);

        var recentPages = await db.GetCollection<Page>(Collections.Pages)
            .Find(FilterDefinition<Page>.Empty)
            .SortByDescending(p => p.DownloadedAt)
            .Limit(SnapshotRecentCount)
            .Project(p => new RecentArticleDto
            {
                PageId = p.PageId,
                Title = p.Title,
                WikiUrl = p.WikiUrl,
                LastModified = p.LastModified,
                DownloadedAt = p.DownloadedAt,
                Continuity = p.Continuity,
            })
            .ToListAsync(ct);

        var recentChunks = await RecentChunksAsync(0, SnapshotRecentCount, ct);

        var corpus = new CorpusStatsDto
        {
            KgNodes = kgNodes.Result,
            KgEdges = kgEdges.Result,
            ArticlePages = pages.Result,
            ArticleChunks = chunks.Result,
            LastWikiSyncUtc = lastSync,
            SnapshotUtc = DateTime.UtcNow,
        };

        logger.LogDebug(
            "Corpus stats snapshot rebuilt: nodes={Nodes} edges={Edges} pages={Pages} chunks={Chunks} lastSync={LastSync:o}",
            corpus.KgNodes,
            corpus.KgEdges,
            corpus.ArticlePages,
            corpus.ArticleChunks,
            corpus.LastWikiSyncUtc
        );

        return new Snapshot(corpus, recentPages, recentChunks.Items);
    }

    // ── Phase 2: uncached, bounded, indexed browse ──────────────────────────────

    public async Task<StatsPage<RecentArticleDto>> BrowsePagesAsync(int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxBrowseTake);
        var coll = Db.GetCollection<Page>(Collections.Pages);

        var total = await coll.EstimatedDocumentCountAsync(cancellationToken: ct); // approx is fine for a browse footer
        var items = await coll.Find(FilterDefinition<Page>.Empty)
            .SortByDescending(p => p.DownloadedAt)
            .Skip(skip)
            .Limit(take)
            .Project(p => new RecentArticleDto
            {
                PageId = p.PageId,
                Title = p.Title,
                WikiUrl = p.WikiUrl,
                LastModified = p.LastModified,
                DownloadedAt = p.DownloadedAt,
                Continuity = p.Continuity,
            })
            .ToListAsync(ct);

        return new StatsPage<RecentArticleDto>
        {
            Items = items,
            Total = total,
            Skip = skip,
            Take = take,
        };
    }

    public Task<StatsPage<RecentChunkDto>> BrowseChunksAsync(int skip, int take, CancellationToken ct) => RecentChunksAsync(skip, take, ct);

    /// <summary>
    /// Recent chunk documents, newest first — a plain index-backed
    /// <c>Find().Sort(createdAt desc).Skip().Limit()</c> over <c>ix_createdAt</c>.
    /// One article chunks into several rows written in a burst, so this reads as
    /// "sections most recently chunked". Used for both the cached recent list
    /// (skip 0) and the Phase-2 paged browse. No whole-collection <c>$group</c>
    /// (Design-037 / ADR-009 option b).
    /// </summary>
    async Task<StatsPage<RecentChunkDto>> RecentChunksAsync(int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxBrowseTake);
        var coll = Db.GetCollection<ArticleChunk>(Collections.SearchChunks);

        var total = await coll.EstimatedDocumentCountAsync(cancellationToken: ct); // approx is fine for a browse footer
        var items = await coll.Find(FilterDefinition<ArticleChunk>.Empty)
            .SortByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .Project(c => new RecentChunkDto
            {
                PageId = c.PageId,
                Title = c.Title,
                WikiUrl = c.WikiUrl,
                Heading = c.Heading,
                CreatedAt = c.CreatedAt,
                Continuity = c.Continuity,
            })
            .ToListAsync(ct);

        return new StatsPage<RecentChunkDto>
        {
            Items = items,
            Total = total,
            Skip = skip,
            Take = take,
        };
    }
}
