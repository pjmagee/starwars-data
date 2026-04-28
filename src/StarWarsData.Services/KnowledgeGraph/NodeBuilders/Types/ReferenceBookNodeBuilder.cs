using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ReferenceBook"/> extraction. Real-world reference
/// books and guides (Star Wars Encyclopedia, Visual Dictionaries, etc.).
///
/// <para>Design-024 Phase C — C3: ISBN appears as an empty-label row (98% of
/// ReferenceBook pages — the highest hit rate of any book-shaped type).
/// <see cref="OnFinalize"/> normalises the empty key to <c>"ISBN"</c> via
/// <see cref="IsbnNormalizer"/>.</para>
/// </summary>
public sealed class ReferenceBookNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ReferenceBook;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => IsbnNormalizer.NormalizeEmptyLabelToIsbn(ctx, node, edges);
}
