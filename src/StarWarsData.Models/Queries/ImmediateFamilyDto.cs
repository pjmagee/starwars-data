namespace StarWarsData.Models.Queries;

public class ImmediateFamilyDto
{
    public FamilyNodeDto Root { get; set; } = null!;
    public List<FamilyNodeDto> Parents { get; set; } = [];
    public List<FamilyNodeDto> Partners { get; set; } = [];
    public List<FamilyNodeDto> Siblings { get; set; } = [];
    public List<FamilyNodeDto> Children { get; set; } = [];
}
