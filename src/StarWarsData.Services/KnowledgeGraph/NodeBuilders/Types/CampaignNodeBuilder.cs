using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Campaign"/> extraction. Sub-war operational arc.
/// No type-specific logic yet.
/// </summary>
public sealed class CampaignNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Campaign;
}
