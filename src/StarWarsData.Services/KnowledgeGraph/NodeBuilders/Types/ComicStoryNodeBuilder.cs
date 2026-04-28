using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ComicStory"/> extraction. Individual stories
/// inside comic issues / collections. Distinct from <see cref="KgNodeTypes.ComicBook"/>
/// (the issue) and <see cref="KgNodeTypes.ComicCollection"/> (anthologies).
/// No type-specific logic yet.
/// </summary>
public sealed class ComicStoryNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ComicStory;
}
