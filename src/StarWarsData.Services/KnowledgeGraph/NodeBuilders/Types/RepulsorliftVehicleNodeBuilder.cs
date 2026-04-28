using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.RepulsorliftVehicle"/> extraction. Speeders,
/// landspeeders, swoop bikes — anything using repulsorlift propulsion.
/// No type-specific logic yet. Shares most field semantics with
/// <see cref="KgNodeTypes.GroundVehicle"/> and <see cref="KgNodeTypes.AirVehicle"/>.
/// </summary>
public sealed class RepulsorliftVehicleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.RepulsorliftVehicle;
}
