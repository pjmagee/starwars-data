using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Queries;

public class QueryParams
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Search { get; set; } = null!;
}

public class TimelineQueryParams
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string[] Categories { get; set; } = [];
    public Continuity? Continuity { get; set; } = null; // null means "both"
    public Universe? Universe { get; set; } = null; // null means "both"
    public float? YearFrom { get; set; }
    public Demarcation? YearFromDemarcation { get; set; }
    public float? YearTo { get; set; }
    public Demarcation? YearToDemarcation { get; set; }
    public string? Search { get; set; }
}
