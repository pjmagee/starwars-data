using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ComicCollection"/> extraction. Trade paperback /
/// hardcover collections of comic stories. No type-specific logic yet.
/// </summary>
public sealed class ComicCollectionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ComicCollection;
}
