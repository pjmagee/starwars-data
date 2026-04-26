using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Region"/> extraction. Galactic regions (Core Worlds,
/// Outer Rim, etc.). No type-specific logic yet.
/// </summary>
public sealed class RegionNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Region;
}
