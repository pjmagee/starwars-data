using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Droid"/> extraction. Specific droids (R2-D2, C-3PO).
///
/// <para>Design-024 Phase B: a Droid's <c>Affiliation</c> field, when the target
/// is a Character/Person, is semantically ownership — droids are property in the
/// Star Wars universe. The survey found 358 such edges on starwars-dev that
/// currently look like generic <c>affiliated_with</c> but mean <c>owned_by</c>.
/// All other Affiliation targets (Government, Organization, Military_unit, …)
/// keep <c>affiliated_with</c>.</para>
/// </summary>
public sealed class DroidNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Droid;

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
            if (targetType is KgNodeTypes.Character or KgNodeTypes.Person)
                edge.Label = "owned_by";
        }
    }
}
