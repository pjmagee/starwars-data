using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Species"/> extraction. Sentient and non-sentient
/// species share the same template. No type-specific logic yet.
/// </summary>
public sealed class SpeciesNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Species;
}
