using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Era"/> extraction. Pure temporal markers (Old
/// Republic era, Imperial era, etc.). Edges TO Era nodes are dropped by
/// the coordinator post-processing — they're metadata, not relationships.
/// </summary>
public sealed class EraNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Era;
}
