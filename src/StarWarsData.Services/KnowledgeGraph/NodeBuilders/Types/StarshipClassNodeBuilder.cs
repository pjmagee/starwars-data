using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.StarshipClass"/> extraction. Ship class / model
/// (X-wing starfighter, Imperial-class Star Destroyer). No type-specific
/// logic yet.
/// </summary>
public sealed class StarshipClassNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.StarshipClass;
}
