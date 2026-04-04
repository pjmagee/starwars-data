namespace StarWarsData.Models.Queries;

public class TemporalNodeDto
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
}

public class BrowseTemporalNodesResult
{
    public List<TemporalNodeDto> Items { get; set; } = [];
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<string> AvailableTypes { get; set; } = [];
}
