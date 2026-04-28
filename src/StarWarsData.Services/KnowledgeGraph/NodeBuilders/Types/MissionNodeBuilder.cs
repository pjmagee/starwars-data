using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Mission"/> extraction. Per-side encoding via
/// <see cref="ConflictSideEncoder"/> — same pattern as
/// <see cref="BattleNodeBuilder"/>. See Design-024 Pattern B.
/// </summary>
public sealed class MissionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Mission;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => ConflictSideEncoder.StampSideIndex(ctx, edges);
}
