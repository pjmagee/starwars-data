namespace StarWarsData.Models;

public class DataValue
{
    public List<string> Values { get; init; } = null!;
    public List<HyperLink> Links { get; init; } = null!;
}