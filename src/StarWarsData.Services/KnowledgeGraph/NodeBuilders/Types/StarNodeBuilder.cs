using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Star"/> extraction. Astronomical stars
/// (red dwarfs, binary systems, supergiants — distinct from
/// <see cref="KgNodeTypes.System"/> which represents the surrounding
/// solar system). No type-specific logic yet.
/// </summary>
public sealed class StarNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Star;
}
