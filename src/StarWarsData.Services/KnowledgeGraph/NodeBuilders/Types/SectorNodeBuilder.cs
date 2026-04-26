using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Sector"/> extraction. Galactic sectors; relies on
/// the generic geographic-containment edge resolution.
/// </summary>
public sealed class SectorNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Sector;
}
