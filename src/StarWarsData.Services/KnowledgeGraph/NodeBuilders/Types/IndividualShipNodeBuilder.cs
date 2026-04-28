using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.IndividualShip"/> extraction. Specific named warships
/// from the Wookieepedia <c>IndividualShip</c> infobox template (Executor,
/// Devastator, …; the Aspire <c>Starship</c> infobox covers smaller vessels
/// like the Millennium Falcon).
///
/// <para>Design-024 Phase C — C5 (Pattern A — source × target relabel): when an
/// IndividualShip's <c>Affiliation</c> field links to a military unit or fleet,
/// the semantic is operational assignment, not generic affiliation. The survey
/// found 421 → Military_unit + 79 → Fleet = 500 such edges via the Affiliation
/// field. Relabel to <c>assigned_to</c> / <c>has_assignment</c>.</para>
///
/// <para>Note: the dominant ship-to-fleet link comes from a separate <c>Navy</c>
/// field which already maps to <c>in_navy</c> via FieldSemantics — that path
/// is left alone. This override is scoped to Affiliation-sourced edges only.</para>
/// </summary>
public sealed class IndividualShipNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.IndividualShip;

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
            if (targetType is KgNodeTypes.MilitaryUnit or KgNodeTypes.Fleet)
            {
                edge.Label = "assigned_to";
            }
        }
    }
}
