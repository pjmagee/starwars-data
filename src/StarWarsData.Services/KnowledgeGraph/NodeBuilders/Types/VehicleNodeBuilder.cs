using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Vehicle"/> extraction. Generic non-starship vehicles
/// (speeders, walkers, transports). No type-specific logic yet.
/// </summary>
public sealed class VehicleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Vehicle;
}
