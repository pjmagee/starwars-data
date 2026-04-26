using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Character"/> extraction.
/// Per <c>eng/design/007-kg-per-type-builders.md</c>, future enhancement:
/// type-specific lifecycle chain assembly (born → trained → fought → died) for
/// richer biography facets. Currently relies on the generic <c>AssignFacetOrder</c>.
/// </summary>
public sealed class CharacterNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Character;
}
