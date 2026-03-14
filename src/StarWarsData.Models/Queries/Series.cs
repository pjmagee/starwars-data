namespace StarWarsData.Models.Queries;

public class Series<T>
{
    public string Name { get; set; } = string.Empty;
    public List<T> Data { get; set; } = new();
}
