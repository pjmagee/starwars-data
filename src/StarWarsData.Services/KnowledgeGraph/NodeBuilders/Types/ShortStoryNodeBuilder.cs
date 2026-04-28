using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ShortStory"/> extraction. No type-specific logic yet.
/// Shares the publication-temporal-fields shape with <see cref="KgNodeTypes.Book"/>
/// and <see cref="KgNodeTypes.ComicStory"/>.
/// </summary>
public sealed class ShortStoryNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ShortStory;
}
