using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Year"/> extraction. Pure temporal markers; edges TO
/// Year nodes are dropped by the coordinator post-processing.
/// </summary>
public sealed class YearNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Year;
}
