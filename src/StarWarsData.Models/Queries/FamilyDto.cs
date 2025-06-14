namespace StarWarsData.Models.Queries;

public class FamilyDto
{
    public string Name { get; set; } = default!;
    public List<string> Members { get; set; } = [];
}
