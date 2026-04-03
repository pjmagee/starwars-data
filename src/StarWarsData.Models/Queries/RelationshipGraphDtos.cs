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
