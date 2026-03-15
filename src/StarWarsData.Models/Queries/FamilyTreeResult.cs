namespace StarWarsData.Models.Queries;

/// <summary>
/// Result of a multi-generation family tree traversal.
/// </summary>
public class FamilyTreeResult
{
    public int RootId { get; init; }

    /// <summary>All discovered nodes keyed by PageId, including the root.</summary>
    public Dictionary<int, FamilyNodeDto> Nodes { get; init; } = [];

    /// <summary>
    /// Directed edges as (fromId, toId) pairs representing parent→child relationships.
    /// Used by the frontend to draw links without re-querying.
    /// </summary>
    public List<FamilyEdge> Edges { get; init; } = [];
}

public class FamilyEdge
{
    public int FromId { get; init; }
    public int ToId   { get; init; }
}
