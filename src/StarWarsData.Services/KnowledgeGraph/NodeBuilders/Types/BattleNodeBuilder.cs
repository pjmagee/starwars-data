using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Battle"/> extraction. Per-side encoding via
/// <see cref="ConflictSideEncoder"/> stamps <c>Meta.SideIndex</c> on edges
/// originating from numbered fields (<c>side1..4</c>, <c>commanders1..4</c>,
/// <c>ppl1..4</c>, <c>unit1..4</c>) and drops commander edges with implausible
/// target types. See Design-024 Pattern B.
/// </summary>
public sealed class BattleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Battle;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        ConflictSideEncoder.StampSideIndex(ctx, edges);
    }
}
