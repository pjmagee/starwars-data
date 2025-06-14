namespace StarWarsData.Models.Queries;

public class PagedChartData<T>
{
    public int PageSize { get; set; }
    public int Page { get; set; }
    public int Total { get; set; }

    public ChartData<T> ChartData { get; set; }
}
