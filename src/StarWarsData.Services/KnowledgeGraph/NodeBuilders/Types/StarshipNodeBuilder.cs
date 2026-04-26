using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Starship"/> extraction. Specific named ships
/// (Millennium Falcon, Executor). Class membership surfaces via the generic
/// relationship loop.
/// </summary>
public sealed class StarshipNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Starship;
}
