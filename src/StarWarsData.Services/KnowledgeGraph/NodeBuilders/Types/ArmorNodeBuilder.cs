using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Armor"/> extraction. Body armor, helmets, shields.
/// No type-specific logic yet.
/// </summary>
public sealed class ArmorNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Armor;
}
