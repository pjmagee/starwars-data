using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Adventure"/> extraction. Tabletop RPG modules
/// and choose-your-own-adventure publications. No type-specific logic yet.
/// </summary>
public sealed class AdventureNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Adventure;
}
