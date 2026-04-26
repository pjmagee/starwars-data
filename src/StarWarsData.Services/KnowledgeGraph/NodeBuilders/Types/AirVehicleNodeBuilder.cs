using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.AirVehicle"/> extraction. Atmospheric craft
/// (airspeeders, gunships). No type-specific logic yet.
/// </summary>
public sealed class AirVehicleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.AirVehicle;
}
