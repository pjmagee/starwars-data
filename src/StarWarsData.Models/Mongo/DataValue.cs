namespace StarWarsData.Models.Mongo;

public class DataValue
{
    public List<string> Values { get; set; } = null!;
    public List<HyperLink> Links { get; set; } = null!;
}