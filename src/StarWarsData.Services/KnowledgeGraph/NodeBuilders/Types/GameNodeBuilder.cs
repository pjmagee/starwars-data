using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Game"/> extraction. Real-world video games and
/// tabletop. No type-specific logic yet.
/// </summary>
public sealed class GameNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Game;
}
