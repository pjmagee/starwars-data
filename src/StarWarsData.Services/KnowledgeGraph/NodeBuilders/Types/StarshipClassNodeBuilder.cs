using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.StarshipClass"/> extraction. Ship class / model
/// (X-wing starfighter, Imperial-class Star Destroyer).
///
/// <para>Design-024 Phase C — C5 (Pattern A — source × target relabel): when a
/// StarshipClass's <c>Affiliation</c> field links to a Religion or Species, the
/// semantic is "designed for / by" — Sith vessels, Wookiee-only ships, etc.
/// The survey found 131 → Religion + 90 → Species = 221 such edges. Relabel
/// to <c>designed_for</c> / <c>design_target_of</c>. Other Affiliation targets
/// (Government, Organization, Military_unit, Fleet) keep
/// <c>affiliated_with</c> — the generic faction-membership semantic still
/// fits those.</para>
/// </summary>
public sealed class StarshipClassNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.StarshipClass;

    static readonly HashSet<string> AffiliationFields = new(StringComparer.OrdinalIgnoreCase) { "Affiliation", "Affiliation(s)" };

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        foreach (var edge in edges)
        {
            var sourceField = edge.Meta?.SourceFieldLabel;
            if (sourceField is null || !AffiliationFields.Contains(sourceField))
                continue;
            if (!string.Equals(edge.Label, "affiliated_with", StringComparison.OrdinalIgnoreCase))
                continue;

            var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
            if (targetType is KgNodeTypes.Religion or KgNodeTypes.Species)
            {
                edge.Label = "designed_for";
            }
        }
    }
}
