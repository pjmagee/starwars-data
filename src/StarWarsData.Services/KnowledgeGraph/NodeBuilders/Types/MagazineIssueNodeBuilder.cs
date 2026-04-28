using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.MagazineIssue"/> extraction. Individual magazine issues.
///
/// <para>Design-024 Phase C — C3: ~12.5% of MagazineIssue pages have an
/// empty-label ISBN row. Lower hit rate than book-shaped types (most magazine
/// issues use ISSN, not ISBN, and the wiki template only emits when an ISBN
/// is set), but the same fix applies. <see cref="OnFinalize"/> normalises the
/// empty key to <c>"ISBN"</c> via <see cref="IsbnNormalizer"/>.</para>
/// </summary>
public sealed class MagazineIssueNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.MagazineIssue;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => IsbnNormalizer.NormalizeEmptyLabelToIsbn(ctx, node, edges);
}
