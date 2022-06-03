namespace StarWarsData.Models;

public class QueryParams
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Search { get; set; } = null!;
}