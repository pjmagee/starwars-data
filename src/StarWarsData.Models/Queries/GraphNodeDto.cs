namespace StarWarsData.Models.Queries;

public class GraphNodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Born { get; set; } = string.Empty;
    public string Died { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>BFS generation relative to the root (0 = root, negative = ancestors, positive = descendants).</summary>
    public int Generation { get; set; }

    public List<int> Parents { get; set; } = [];
    public List<int> Partners { get; set; } = [];
    public List<int> Siblings { get; set; } = [];
    public List<int> Children { get; set; } = [];
}
