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
    public int ToId { get; init; }
    public string Label { get; init; } = "child";
}

/// <summary>
/// Configures which infobox labels to use as relationship sources
/// and how they affect generation layout.
/// </summary>
public class RelationshipLabels
{
    /// <summary>Labels where linked entities are one generation above (e.g. Parent(s), Masters).</summary>
    public List<string> UpLabels { get; init; } = [];

    /// <summary>Labels where linked entities are one generation below (e.g. Children, Apprentices).</summary>
    public List<string> DownLabels { get; init; } = [];

    /// <summary>Labels where linked entities are the same generation (e.g. Partner(s), Sibling(s)).</summary>
    public List<string> PeerLabels { get; init; } = [];

    public List<string> AllLabels => [.. UpLabels, .. DownLabels, .. PeerLabels];
}
