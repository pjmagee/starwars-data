namespace StarWarsData.Models;

public class ChartData
{
    public List<Series<int>> Series { get; set; }
    public string[] Labels { get; set; }
}

public class Series<T>
{
    public string Name { get; set; }
    public List<T> Data { get; set; }
}

public class PagedChartData
{
    public int PageSize { get; set; }
    public int Page { get; set; }
    public int Total { get; set; }

    public ChartData ChartData { get; set; }
}