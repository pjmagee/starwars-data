using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ForcePower"/> extraction. Qualifier nodes for Force
/// abilities (Force lightning, mind trick). Edges TO ForcePower on
/// person-relationship labels are dropped by the coordinator post-processing.
/// </summary>
public sealed class ForcePowerNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ForcePower;
}
