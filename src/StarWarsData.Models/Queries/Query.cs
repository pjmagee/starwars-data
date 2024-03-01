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
}