using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Clothing"/> extraction. Garments distinct from
/// <see cref="KgNodeTypes.Armor"/>. No type-specific logic yet.
/// </summary>
public sealed class ClothingNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Clothing;
}
