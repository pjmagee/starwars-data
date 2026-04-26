using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Election"/> extraction. Senate, chancellorship, and
/// equivalent governance contests. No type-specific logic yet.
/// </summary>
public sealed class ElectionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Election;
}
