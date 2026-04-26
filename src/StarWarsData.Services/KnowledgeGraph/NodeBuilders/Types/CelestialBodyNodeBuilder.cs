using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.CelestialBody"/> extraction.
/// Per <c>eng/design/006-galaxy-map-timeline-mode.md</c>, future enhancement:
/// emit destruction events as discrete temporal markers on the galaxy map
/// timeline (e.g. Alderaan's destruction in 0 BBY). Currently surfaces via
/// the generic destruction-temporal field mapping.
/// </summary>
public sealed class CelestialBodyNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.CelestialBody;
}
