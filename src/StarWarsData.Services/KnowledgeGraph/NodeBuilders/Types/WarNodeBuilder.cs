using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.War"/> extraction. Multi-battle conflicts; relies
/// on the generic <c>part_of</c> edge resolution for membership.
/// </summary>
public sealed class WarNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.War;
}
