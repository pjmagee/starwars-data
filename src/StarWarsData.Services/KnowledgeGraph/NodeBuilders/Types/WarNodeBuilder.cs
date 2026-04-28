using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.War"/> extraction. Per-side encoding via
/// <see cref="ConflictSideEncoder"/> — same pattern as
/// <see cref="BattleNodeBuilder"/>. See Design-024 Pattern B.
/// </summary>
public sealed class WarNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.War;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => ConflictSideEncoder.StampSideIndex(ctx, edges);
}
