namespace StarWarsData.Models.Entities;

/// <summary>
/// Snapshot of the daily incremental wiki sync for the admin dashboard:
/// when it last ran and the pages most recently (re)downloaded by it.
/// </summary>
public class RecentSyncStatus
{
    /// <summary>UpdatedAt of the <c>IncrementalSync</c> job_state doc, or null if it has never run.</summary>
    public DateTime? LastIncrementalSyncAt { get; init; }

    public List<RecentSyncedPage> Pages { get; init; } = [];
}

public class RecentSyncedPage
{
    public int PageId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string WikiUrl { get; init; } = string.Empty;

    /// <summary>When this page was last fetched. The incremental sync sets this to now for every changed page it re-pulls.</summary>
    public DateTime DownloadedAt { get; init; }

    /// <summary>Wiki revision timestamp (when the article itself last changed upstream).</summary>
    public DateTime LastModified { get; init; }

    public string Continuity { get; init; } = string.Empty;
}
