using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.GroundVehicle"/> extraction. Surface craft
/// (AT-AT, AT-ST, tanks). No type-specific logic yet.
/// </summary>
public sealed class GroundVehicleNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.GroundVehicle;
}
