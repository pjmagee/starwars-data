namespace StarWarsData.Models.Queries;

/// <summary>
/// Overall progress summary for the relationship graph builder dashboard.
/// </summary>
public class GraphBuilderProgress
{
    public int TotalPages { get; init; }
    public int ProcessedPages { get; init; }
    public int SkippedPages { get; init; }
    public int FailedPages { get; init; }
    public int PendingPages { get; init; }
    public long TotalEdges { get; init; }
    public int TotalLabels { get; init; }

    /// <summary>Pages processed per hour over the last hour.</summary>
    public double PagesPerHour { get; init; }

    /// <summary>Estimated hours remaining at current throughput.</summary>
    public double? EstimatedHoursRemaining { get; init; }

    public List<TypeProgress> ByType { get; init; } = [];
    public List<RecentLabel> RecentLabels { get; init; } = [];
}

public class TypeProgress
{
    public string Type { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
}

public class RecentLabel
{
    public string Label { get; init; } = string.Empty;
    public string Reverse { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int UsageCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Result of a $graphLookup traversal from the persistent relationship graph.
/// </summary>
public class RelationshipGraphResult
{
    public int RootId { get; init; }
    public string RootName { get; init; } = string.Empty;
    public List<RelationshipGraphNode> Nodes { get; init; } = [];
    public List<RelationshipGraphEdge> Edges { get; init; } = [];
}

public class RelationshipGraphNode
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
}

public class RelationshipGraphEdge
{
    public int FromId { get; init; }
    public int ToId { get; init; }
    public string Label { get; init; } = string.Empty;
    public double Weight { get; init; }
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
}

/// <summary>
/// The relationship labels available on a node plus a pre-computed set of
/// "default enabled" labels (the subset the UI should turn on initially for
/// that node's entity type to avoid noisy graphs).
/// </summary>
public class EntityLabelsResult
{
    /// <summary>Entity type (Character, Battle, Organization, ...). Empty if unknown.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>All distinct relationship labels for the node (outgoing + reversed inbound).</summary>
    public List<string> Labels { get; init; } = [];

    /// <summary>
    /// Subset of <see cref="Labels"/> that should be enabled by default when rendering
    /// a graph for this node — typically the labels whose semantic category is
    /// prioritized for the node's entity type.
    /// </summary>
    public List<string> DefaultEnabled { get; init; } = [];
}

/// <summary>
/// Paginated browse result for processed entities in the relationship graph.
/// </summary>
public class BrowseEntitiesResult
{
    public List<EntitySearchDto> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// Progress summary for the article chunking dashboard.
/// </summary>
/// <summary>
/// Summary of an OpenAI Batch API job for the dashboard.
/// </summary>
public class GraphBatchSummary
{
    public string Id { get; init; } = string.Empty;
    public string OpenAiBatchId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int TotalRequests { get; init; }
    public int CompletedRequests { get; init; }
    public int FailedRequests { get; init; }
    public int SkippedRequests { get; init; }
    public int TotalEdgesStored { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? ProcessedAt { get; init; }
    public string? Error { get; init; }
}

public class ChunkingProgress
{
    public int TotalEligiblePages { get; init; }
    public int ChunkedPages { get; init; }
    public int PendingPages { get; init; }
    public long TotalChunks { get; init; }
    public double AvgChunksPerPage { get; init; }
    public double PagesPerHour { get; init; }
    public double? EstimatedHoursRemaining { get; init; }
    public List<ChunkingTypeProgress> ByType { get; init; } = [];
}

public class ChunkingTypeProgress
{
    public string Type { get; init; } = string.Empty;
    public int Pages { get; init; }
    public long Chunks { get; init; }
    public double AvgChunksPerPage { get; init; }
}

/// <summary>
/// Aggregated stats for a single relationship label across kg.edges.
/// One row = one directed label. Counts are directional (not pair-counts).
/// </summary>
public class EdgeLabelStatsDto
{
    public string Label { get; init; } = string.Empty;
    public long Count { get; init; }
    public long CanonCount { get; init; }
    public long LegendsCount { get; init; }
    public double AvgWeight { get; init; }

    /// <summary>Top source entity types with counts (max 5).</summary>
    public List<TypeCount> TopFromTypes { get; init; } = [];

    /// <summary>Top target entity types with counts (max 5).</summary>
    public List<TypeCount> TopToTypes { get; init; } = [];

    /// <summary>One example edge for context.</summary>
    public EdgeSample? Sample { get; init; }
}

public class TypeCount
{
    public string Type { get; init; } = string.Empty;
    public long Count { get; init; }
}

public class EdgeSample
{
    public int FromId { get; init; }
    public string FromName { get; init; } = string.Empty;
    public int ToId { get; init; }
    public string ToName { get; init; } = string.Empty;
}

public class BrowseEdgeLabelsResult
{
    public List<EdgeLabelStatsDto> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
