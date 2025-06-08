namespace StarWarsData.Models.Queries;

public class ImmediateFamilyDto
{
    public FamilyNodeDto Root { get; set; } = null!;
    public List<FamilyNodeDto> Parents { get; set; } = new();
    public List<FamilyNodeDto> Partners { get; set; } = new();
    public List<FamilyNodeDto> Siblings { get; set; } = new();
    public List<FamilyNodeDto> Children { get; set; } = new();
}
