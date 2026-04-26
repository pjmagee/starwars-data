using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Book"/> extraction. Real-world novels and reference
/// books; uses the real-world calendar for release-date facets. No
/// type-specific logic yet.
/// </summary>
public sealed class BookNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Book;
}
