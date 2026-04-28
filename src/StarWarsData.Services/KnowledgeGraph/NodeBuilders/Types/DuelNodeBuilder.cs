using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Duel"/> extraction. Per-side encoding via
/// <see cref="ConflictSideEncoder"/> — same pattern as
/// <see cref="BattleNodeBuilder"/>. See Design-024 Pattern B.
/// </summary>
public sealed class DuelNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Duel;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => ConflictSideEncoder.StampSideIndex(ctx, edges);
}
