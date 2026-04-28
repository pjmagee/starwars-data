using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ComicBook"/> extraction. Individual comic-book issues.
///
/// <para>Design-024 Phase C — C3: ISBN appears as an empty-label row on ~22.5%
/// of ComicBook pages (less common than Book/ReferenceBook because most
/// individual issues don't carry an ISBN, only collected editions do).
/// <see cref="OnFinalize"/> normalises the empty key to <c>"ISBN"</c> via
/// <see cref="IsbnNormalizer"/>.</para>
/// </summary>
public sealed class ComicBookNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ComicBook;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges) => IsbnNormalizer.NormalizeEmptyLabelToIsbn(ctx, node, edges);
}
