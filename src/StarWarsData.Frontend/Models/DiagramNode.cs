using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace StarWarsData.Frontend.Models;

public class DiagramNode : NodeModel
{
    public string Label { get; }
    public List<(string Key, string Value)> Attributes { get; }

    public DiagramNode(
        string label,
        List<(string Key, string Value)> attributes,
        Point? position = null
    )
        : base(position)
    {
        Label = label;
        Attributes = attributes;
    }
}
