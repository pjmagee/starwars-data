using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Stats;

/// <summary>
/// Design-037 / ADR-009: public, read-only corpus health snapshot. Whole-corpus
/// totals + last wiki-sync time. Deliberately continuity-agnostic (global-filter
/// exempt — these are infrastructure figures, not content).
/// </summary>
public sealed class CorpusStatsDto
{
    public long KgNodes { get; init; }
    public long KgEdges { get; init; }
    public long ArticlePages { get; init; }
    public long ArticleChunks { get; init; }

    /// <summary><c>raw.job_state</c> <c>IncrementalSync</c> <c>UpdatedAt</c>, or null if it has never run.</summary>
    public DateTime? LastWikiSyncUtc { get; init; }

    /// <summary>When this (cached) snapshot was built — surfaced so the UI can say "updated X ago".</summary>
    public DateTime SnapshotUtc { get; init; }
}

/// <summary>A recently (re)synced wiki article. Public data only (titles are public Wookieepedia content).</summary>
public sealed class RecentArticleDto
{
    public int PageId { get; init; }
    public string Title { get; init; } = "";
    public string WikiUrl { get; init; } = "";

    /// <summary>Wiki revision timestamp (when the article itself last changed upstream).</summary>
    public DateTime LastModified { get; init; }

    /// <summary>When our incremental sync last fetched it.</summary>
    public DateTime DownloadedAt { get; init; }

    public Continuity Continuity { get; init; } = Continuity.Unknown;
}

/// <summary>A recently chunked article (one row per page, aggregated over its chunks).</summary>
public sealed class RecentChunkDto
{
    public int PageId { get; init; }
    public string Title { get; init; } = "";
    public string WikiUrl { get; init; } = "";
    public int ChunkCount { get; init; }

    /// <summary>Most recent <c>ArticleChunk.CreatedAt</c> across this page's chunks.</summary>
    public DateTime CreatedAt { get; init; }

    public Continuity Continuity { get; init; } = Continuity.Unknown;
}

/// <summary>
/// Generic page of results for the Phase-2 read-only browse endpoints. Named
/// distinctly (not <c>PagedResult</c>) to avoid colliding with the existing
/// <c>StarWarsData.Models.Queries.PagedResult&lt;T&gt;</c>.
/// </summary>
public sealed class StatsPage<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public long Total { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}
