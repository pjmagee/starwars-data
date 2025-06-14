namespace StarWarsData.Models.Queries;

public class CharacterRelationsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string Born { get; set; } = string.Empty;
    public string Died { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;

    public List<string> Family { get; set; } = [];
    public List<int> Parents { get; set; } = [];
    public List<int> Partners { get; set; } = [];
    public List<int> Siblings { get; set; } = [];
    public List<int> Children { get; set; } = [];
}
