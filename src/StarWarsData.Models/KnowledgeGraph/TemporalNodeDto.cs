namespace StarWarsData.Models.Queries;

public class TemporalNodeDto : IEquatable<TemporalNodeDto>
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Continuity { get; set; }
    public string? ImageUrl { get; set; }
    public string? WikiUrl { get; set; }
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public string? StartDateText { get; set; }
    public string? EndDateText { get; set; }
    public Dictionary<string, List<string>> Properties { get; set; } = new();
    public List<StarWarsData.Models.Entities.TemporalFacet> TemporalFacets { get; set; } = [];

    /// <summary>
    /// Per-property markers for active Holocron enrichments on this node — the Phase 2
    /// provenance signal the UI uses to differentiate infobox-derived data from
    /// agent-added context. Empty when no active enrichment exists.
    /// One marker per <c>(fieldPath, operation)</c>; consumers match by <c>FieldPath</c>
    /// against keys of <see cref="Properties"/>. See Design-019 for the rendering rules.
    /// </summary>
    public List<EnrichmentMarkerDto> EnrichmentMarkers { get; set; } = [];

    // Identity-based equality so multi-selection in MudTable survives page reloads —
    // each LoadData call produces fresh DTO instances, but two DTOs representing
    // the same node (same PageId) are treated as equal for HashSet/SelectedItems.
    public bool Equals(TemporalNodeDto? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => obj is TemporalNodeDto d && Equals(d);

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// Per-property provenance signal for the Knowledge Graph node-detail panel.
/// <c>Operation</c> is one of <c>"Add"</c> / <c>"Augment"</c> / <c>"FillGap"</c> /
/// <c>"Annotate"</c> — the same enum as the agent's <c>EnrichmentOperation</c>,
/// serialized as a string for transport. Pre-flight rules guarantee that for node
/// properties only <c>Add</c> and <c>Augment</c> appear (FillGap/Annotate are
/// edge-only operations).
/// </summary>
public record EnrichmentMarkerDto(string FieldPath, string Operation);

public class BrowseTemporalNodesResult
{
    public List<TemporalNodeDto> Items { get; set; } = [];
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<string> AvailableTypes { get; set; } = [];
}
