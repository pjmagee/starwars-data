using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Family"/> extraction. Family lineage closures are
/// computed by the coordinator's <c>ComputeLineageClosures</c> pass — this
/// builder owns only the per-page extraction.
/// </summary>
public sealed class FamilyNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Family;
}
