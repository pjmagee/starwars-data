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
    public Realm? Realm { get; set; } = null; // null means "both"

    /// <summary>
    /// Calendar mode: <see cref="Queries.Calendar.Galactic"/> (BBY/ABY, default for the
    /// Timeline page), <see cref="Queries.Calendar.Real"/> (signed CE year), or null for
    /// both. When <see cref="Queries.Calendar.Galactic"/>, <see cref="YearFrom"/>/<see cref="YearTo"/>
    /// are paired with the Demarcation properties. When <see cref="Queries.Calendar.Real"/>,
    /// they are signed CE ints and the Demarcation properties are ignored.
    /// </summary>
    public Calendar? Calendar { get; set; } = null;
    public float? YearFrom { get; set; }
    public Demarcation? YearFromDemarcation { get; set; }
    public float? YearTo { get; set; }
    public Demarcation? YearToDemarcation { get; set; }
    public string? Search { get; set; }
}
