using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Treaty"/> extraction. Diplomatic agreements;
/// signatories surface via the generic relationship loop.
/// </summary>
public sealed class TreatyNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Treaty;
}
