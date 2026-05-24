using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.CelestialBody"/> extraction.
///
/// <para>Design-024 Phase C (Pattern A — source-aware relabel): the canonical
/// <c>Language(s)</c> field maps globally to <c>speaks_language</c>, which is
/// awkward when the source is a planet — planets don't speak. The survey shows
/// CelestialBody is the dominant source for that label (~1,649 of ~2,749 edges).
/// This <see cref="OnFinalize"/> override relabels CelestialBody → Language
/// edges to <c>has_language</c> / <c>language_of</c>. Other source types
/// (Species, System, etc.) keep <c>speaks_language</c> — those readings are
/// correct.</para>
///
/// <para>Per <c>specs/006-galaxy-map-timeline-mode/spec.md</c>, future enhancement:
/// emit destruction events as discrete temporal markers on the galaxy map
/// timeline (e.g. Alderaan's destruction in 0 BBY). Currently surfaces via
/// the generic destruction-temporal field mapping.</para>
/// </summary>
public sealed class CelestialBodyNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.CelestialBody;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (!string.Equals(edge.Label, "speaks_language", StringComparison.OrdinalIgnoreCase))
                continue;

            var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
            if (string.Equals(targetType, KgNodeTypes.Language, StringComparison.OrdinalIgnoreCase))
            {
                edge.Label = "has_language";
            }
        }
    }
}
