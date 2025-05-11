using System.Collections.Generic;

namespace StarWarsData.Models.Queries;

public class CharacterRelationsDto
{
    public string Name { get; set; } = default!;
    public string Born { get; set; } = string.Empty;
    public string Died { get; set; } = string.Empty;
    
    public string ImageUrl { get; set; } = string.Empty;
    public List<string> Family { get; set; } = new();
    public List<string> Parents { get; set; } = new();
    public List<string> Partners { get; set; } = new();
    public List<string> Siblings { get; set; } = new();
    public List<string> Children { get; set; } = new();
}
