namespace StarWarsData.Models.Queries;

public class EntitySearchDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Continuity { get; set; }
}
