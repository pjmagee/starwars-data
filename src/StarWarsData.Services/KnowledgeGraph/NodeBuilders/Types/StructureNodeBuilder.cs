using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Structure"/> extraction. Buildings, fortresses,
/// installations. No type-specific logic yet.
/// </summary>
public sealed class StructureNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Structure;
}
