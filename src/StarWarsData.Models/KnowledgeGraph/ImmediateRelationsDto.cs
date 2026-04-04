namespace StarWarsData.Models.Queries;

public class ImmediateRelationsDto
{
    public GraphNodeDto Root { get; set; } = null!;
    public List<GraphNodeDto> Parents { get; set; } = [];
    public List<GraphNodeDto> Partners { get; set; } = [];
    public List<GraphNodeDto> Siblings { get; set; } = [];
    public List<GraphNodeDto> Children { get; set; } = [];
}
