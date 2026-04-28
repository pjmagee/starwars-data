using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Plant"/> extraction. Flora — distinct from Species
/// (which covers sentient and animal life). No type-specific logic yet.
/// </summary>
public sealed class PlantNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Plant;
}
