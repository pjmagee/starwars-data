using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Book"/> extraction. Real-world novels and reference
/// books; uses the real-world calendar for release-date facets.
///
/// <para>Design-024 Phase C — C3: ISBN appears as an empty-label row in the
/// infobox (Wookieepedia template emits ISBN with no <c>Label</c>). 92.5% of
/// Book pages have one. <see cref="OnFinalize"/> renames <c>properties[""]</c>
/// to <c>properties["ISBN"]</c> via <see cref="IsbnNormalizer"/>.</para>
/// </summary>
public sealed class BookNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Book;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => IsbnNormalizer.NormalizeEmptyLabelToIsbn(ctx, node, edges);
}
