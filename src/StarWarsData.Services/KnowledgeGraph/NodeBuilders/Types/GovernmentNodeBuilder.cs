using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Government"/> extraction.
/// Per <c>eng/design/007-kg-per-type-builders.md</c>, future enhancement:
/// type-specific institutional lifecycle assembly (established → reorganized →
/// fragmented → restored → dissolved). Currently relies on the generic
/// <c>AssignFacetOrder</c> for the institutional dimension.
/// </summary>
public sealed class GovernmentNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Government;
}
