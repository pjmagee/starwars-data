using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Nebula"/> extraction. Stellar nebulae; relies on
/// the generic geographic-containment edge resolution.
/// </summary>
public sealed class NebulaNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Nebula;
}
