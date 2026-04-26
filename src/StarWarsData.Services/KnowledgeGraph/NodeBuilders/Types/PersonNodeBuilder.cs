using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Person"/> extraction. Real-world equivalent of
/// <see cref="CharacterNodeBuilder"/> — used for actors, authors, etc.
/// No type-specific logic yet.
/// </summary>
public sealed class PersonNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Person;
}
