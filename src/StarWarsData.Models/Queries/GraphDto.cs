namespace StarWarsData.Models.Queries;

public class GraphDto
{
    public int RootId { get; set; }
    public List<GraphNodeDto> Nodes { get; set; } = [];
}
