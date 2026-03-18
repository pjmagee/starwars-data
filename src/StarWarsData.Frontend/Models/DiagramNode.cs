using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace StarWarsData.Frontend.Models;

public class DiagramNode : NodeModel
{
    public string Label { get; }
    public List<(string Key, string Value)> Attributes { get; }
    public string? ImageUrl { get; }
    public bool IsRoot { get; }

    public DiagramNode(
        string label,
        List<(string Key, string Value)> attributes,
        string? imageUrl = null,
        bool isRoot = false,
        Point? position = null
    )
        : base(position)
    {
        Label = label;
        Attributes = attributes;
        ImageUrl = imageUrl;
        IsRoot = isRoot;
    }
}
