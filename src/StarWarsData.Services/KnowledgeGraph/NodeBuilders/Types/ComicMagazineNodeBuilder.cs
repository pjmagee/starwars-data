using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ComicMagazine"/> extraction. Periodical comic
/// publications (Star Wars Tales magazine etc.). No type-specific logic yet.
/// </summary>
public sealed class ComicMagazineNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ComicMagazine;
}
