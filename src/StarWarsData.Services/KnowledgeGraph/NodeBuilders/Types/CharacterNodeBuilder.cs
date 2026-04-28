using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Character"/> extraction.
///
/// <para>Design-024 Phase B: <c>Affiliation(s)</c> is the most overloaded infobox field
/// on Character pages — 50K+ edges spanning a dozen target types with one shared
/// label (<c>affiliated_with</c>). <see cref="OnFinalize"/> reroutes the relationship
/// per (sourceType=Character × targetType) so the canonical label carries the
/// real semantics:</para>
/// <list type="bullet">
///   <item>→ TitleOrPosition: <c>has_role</c> / <c>held_by</c></item>
///   <item>→ Family: <c>member_of_family</c> / <c>has_family_member</c></item>
///   <item>→ Religion: <c>member_of</c> / <c>has_member</c></item>
///   <item>→ Species: <c>has_ethnicity</c> / <c>ethnicity_of</c></item>
///   <item>→ City: <c>from_city</c> / <c>origin_city_of</c></item>
///   <item>→ Company: <c>works_for</c> / <c>employs</c></item>
///   <item>→ Military_unit / Fleet: <c>serves_in</c> / <c>has_member</c></item>
/// </list>
/// <para>Other targets (Government, Organization, Structure, Character, …) keep
/// the original <c>affiliated_with</c> — the generic label still fits.</para>
///
/// <para>Each relabel is a clean replacement: the <c>affiliated_with</c> label is
/// gone for that triple. <c>SourceFieldLabel</c> is preserved so audit can still
/// trace the originating infobox row.</para>
/// </summary>
public sealed class CharacterNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Character;

    /// <summary>
    /// Field labels (case-insensitive) on Character infoboxes that mean
    /// "this character belongs to / is affiliated with". Both singular and
    /// (s)-suffix variants exist in the wiki — wikis are permissive.
    /// </summary>
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
            switch (targetType)
            {
                case KgNodeTypes.TitleOrPosition:
                    edge.Label = "has_role";
                    break;
                case KgNodeTypes.Family:
                    edge.Label = "member_of_family";
                    break;
                case KgNodeTypes.Religion:
                    edge.Label = "member_of";
                    break;
                case KgNodeTypes.Species:
                    edge.Label = "has_ethnicity";
                    break;
                case KgNodeTypes.City:
                    edge.Label = "from_city";
                    break;
                case KgNodeTypes.Company:
                    edge.Label = "works_for";
                    break;
                case KgNodeTypes.MilitaryUnit:
                case KgNodeTypes.Fleet:
                    edge.Label = "serves_in";
                    break;
                // default: leave affiliated_with alone — it's correct for Government,
                // Organization, Structure, Character, and other target types.
            }
        }
    }
}
