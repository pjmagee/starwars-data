using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Fleet"/> extraction. Naval formations larger than
/// individual squadrons. Phase B's CharacterNodeBuilder relabels
/// <c>Character → Fleet</c> Affiliation edges to <c>serves_in</c>; this stub
/// covers the Fleet node side itself. No type-specific logic yet.
/// </summary>
public sealed class FleetNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Fleet;
}
