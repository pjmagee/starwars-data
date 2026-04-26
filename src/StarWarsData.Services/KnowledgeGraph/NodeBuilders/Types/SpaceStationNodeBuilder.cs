using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.SpaceStation"/> extraction. Death Stars, Starkiller
/// Base, etc. Destruction events are handled via the generic temporal field
/// loop. No type-specific logic yet.
/// </summary>
public sealed class SpaceStationNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.SpaceStation;
}
