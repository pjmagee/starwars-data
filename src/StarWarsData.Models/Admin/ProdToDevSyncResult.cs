namespace StarWarsData.Models.Entities;

/// <summary>
/// Outcome of a Design-033 prod → dev raw-page refresh. Populated by
/// <c>ProdToDevSyncService.RefreshRawPagesAsync</c> and surfaced in the
/// Hangfire job result / structured logs.
/// </summary>
public sealed class ProdToDevSyncResult
{
    /// <summary><c>"full"</c> for a full mirror, or <c>"since-{n}d"</c> for a recent slice.</summary>
    public required string Mode { get; init; }

    public required string SourceDatabase { get; init; }

    public required string TargetDatabase { get; init; }

    /// <summary>
    /// Count of prod <c>raw.pages</c> documents that matched the slice filter and were
    /// merged into dev. For a full mirror this is the entire prod collection.
    /// </summary>
    public long PagesMerged { get; init; }

    /// <summary>True when <c>?wipe=true</c> emptied dev's <c>raw.pages</c> before the merge (true mirror).</summary>
    public bool Wiped { get; init; }

    /// <summary>Documents removed by the pre-merge wipe (0 when <see cref="Wiped"/> is false).</summary>
    public long DeletedBeforeMerge { get; init; }

    public DateTime StartedUtc { get; init; }

    public double ElapsedSeconds { get; init; }
}
