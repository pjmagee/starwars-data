namespace StarWarsData.Models.Queries;

/// <summary>
/// Result of a multi-generation relationship graph traversal.
/// </summary>
public class GraphResult
{
    public int RootId { get; init; }

    /// <summary>All discovered nodes keyed by PageId, including the root.</summary>
    public Dictionary<int, GraphNodeDto> Nodes { get; init; } = [];

    /// <summary>
    /// Directed edges as (fromId, toId) pairs representing parent→child or other directed relationships.
    /// Used by the frontend to draw links without re-querying.
    /// </summary>
    public List<GraphEdge> Edges { get; init; } = [];
}

public class GraphEdge
{
    public int FromId { get; init; }
    public int ToId   { get; init; }
}
