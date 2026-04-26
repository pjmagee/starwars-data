using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Military"/> extraction. Armed forces, fleets,
/// legions, divisions. No type-specific logic yet.
/// </summary>
public sealed class MilitaryNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Military;
}
