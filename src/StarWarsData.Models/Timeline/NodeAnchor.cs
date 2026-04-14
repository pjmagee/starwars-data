using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Queries;

/// <summary>
/// Temporal anchor for the /timeline/{nodeId} route.
/// Wraps a knowledge-graph node's lifecycle range + identity so the frontend
/// can render the anchored timeline page without a second round-trip.
/// See Design-014 Phase 2.
/// </summary>
public class NodeAnchor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Continuity Continuity { get; set; }
    public string? ImageUrl { get; set; }
    public string? WikiUrl { get; set; }

    public List<NodeAnchorDimension> Dimensions { get; set; } = [];

    /// <summary>Key of the dimension the UI should select by default.</summary>
    public string DefaultDimension { get; set; } = "default";
}

public class NodeAnchorDimension
{
    /// <summary>Stable key (e.g. "default", "lifespan", "conflict"). Used in ?semantic= query.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label ("Lifetime", "Hostilities", "Active period").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Galactic sort-key year (negative = BBY, positive = ABY).</summary>
    public int YearFrom { get; set; }
    public int YearTo { get; set; }

    public Demarcation FromDemarcation { get; set; }
    public Demarcation ToDemarcation { get; set; }

    /// <summary>Raw text from the infobox (may be null for derived ranges).</summary>
    public string? FromText { get; set; }
    public string? ToText { get; set; }
}
