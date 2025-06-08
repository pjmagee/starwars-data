namespace StarWarsData.Models.Queries;

public class FamilyNodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Born { get; set; } = string.Empty;
    public string Died { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;

    public List<int> Parents { get; set; } = new();
    public List<int> Partners { get; set; } = new();
    public List<int> Siblings { get; set; } = new();
    public List<int> Children { get; set; } = new();
}
