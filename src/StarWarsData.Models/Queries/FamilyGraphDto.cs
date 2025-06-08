namespace StarWarsData.Models.Queries;

public class FamilyGraphDto
{
    public int RootId { get; set; }
    public List<FamilyNodeDto> Nodes { get; set; } = new();
}