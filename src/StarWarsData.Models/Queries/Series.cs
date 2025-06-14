namespace StarWarsData.Models.Queries;

public class Series<T>
{
    public string Name { get; set; }
    public List<T> Data { get; set; }
}
