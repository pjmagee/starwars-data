using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Movie"/> extraction. Real-world films; uses the
/// real-world calendar for release-date facets. No type-specific logic yet.
/// </summary>
public sealed class MovieNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Movie;
}
