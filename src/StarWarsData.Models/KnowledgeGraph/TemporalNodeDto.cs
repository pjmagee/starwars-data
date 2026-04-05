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

    // Identity-based equality so multi-selection in MudTable survives page reloads —
    // each LoadData call produces fresh DTO instances, but two DTOs representing
    // the same node (same PageId) are treated as equal for HashSet/SelectedItems.
    public bool Equals(TemporalNodeDto? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => obj is TemporalNodeDto d && Equals(d);

    public override int GetHashCode() => Id.GetHashCode();
}

public class BrowseTemporalNodesResult
{
    public List<TemporalNodeDto> Items { get; set; } = [];
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<string> AvailableTypes { get; set; } = [];
}
