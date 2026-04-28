using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Event"/> extraction. Per-side encoding via
/// <see cref="ConflictSideEncoder"/> for events that use the numbered side
/// fields (some Event-template pages reuse the conflict-style infobox).
/// See Design-024 Pattern B.
/// </summary>
public sealed class EventNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Event;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => ConflictSideEncoder.StampSideIndex(ctx, edges);
}
