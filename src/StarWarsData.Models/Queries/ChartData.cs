namespace StarWarsData.Models.Queries;

public class ChartData<T>
{
    public List<Series<T>> Series { get; set; }
    public string[] Labels { get; set; }
}