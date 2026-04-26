using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Mission"/> extraction. Single-objective operations.
/// No type-specific logic yet.
/// </summary>
public sealed class MissionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Mission;
}
