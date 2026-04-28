using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.MagazineArticle"/> extraction. Individual articles
/// inside reference magazines and tie-in publications. Distinct from
/// <see cref="KgNodeTypes.MagazineIssue"/> (the issue) and
/// <see cref="KgNodeTypes.ReferenceMagazine"/> (the magazine series).
/// No type-specific logic yet.
/// </summary>
public sealed class MagazineArticleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.MagazineArticle;
}
