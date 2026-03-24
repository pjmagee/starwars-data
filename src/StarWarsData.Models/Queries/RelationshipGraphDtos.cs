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
}
