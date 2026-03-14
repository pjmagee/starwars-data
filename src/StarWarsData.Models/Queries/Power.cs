namespace StarWarsData.Models.Queries;

public class Power
{
    public string Name { get; set; } = string.Empty;
    public List<string> Alignments { get; set; } = new();
    public List<string> Areas { get; set; } = new();
}
