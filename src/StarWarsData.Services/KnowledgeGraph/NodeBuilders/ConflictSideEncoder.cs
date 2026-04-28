using System.Text.RegularExpressions;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Shared per-side encoding helper for conflict-type NodeBuilders
/// (Battle, Mission, Duel, War, Campaign, Event).
///
/// <para>Wookieepedia's combat infoboxes encode each belligerent side using a
/// numbered field-name pattern: <c>side1..4</c>, <c>commanders1..4</c>,
/// <c>ppl1..4</c>, <c>unit1..4</c>. The generic loop emits one edge per link
/// but loses the side index — so questions like "who commanded the Separatists
/// at Geonosis" can't be answered without joining through <c>belligerent</c>
/// edges to identify which side was Separatist.</para>
///
/// <para>Design-024 Pattern B: each per-type builder calls
/// <see cref="StampSideIndex"/> from <c>OnFinalize</c>. The helper parses the
/// trailing digit from <c>edge.Meta.SourceFieldLabel</c> and stamps
/// <c>edge.Meta.SideIndex</c>.</para>
///
/// <para>Also filters out commander edges with implausible target types
/// (<c>Religion, CelestialBody, Species, Family, Company</c>) — the survey
/// found ~10% of <c>commanders</c> edges target invalid types as a result of
/// MediaWiki link parsing noise on multi-link values like
/// <c>"Sith Order's Darth Maul"</c>. Drop them rather than silently flag.</para>
/// </summary>
internal static class ConflictSideEncoder
{
    static readonly Regex SideSuffix = new(@"^(commanders|ppl|unit|side)([1-4])$", RegexOptions.Compiled);

    /// <summary>
    /// Target types that are never plausible commanders. The wiki's combat
    /// infobox parser has been observed to emit these as commander edges via
    /// noise links inside qualifier text. The set is intentionally small — only
    /// types that cannot literally command a battle (a <c>Religion</c> doesn't
    /// command, a religion's leader does; a <c>CelestialBody</c> doesn't command;
    /// etc.). Larger types like <c>Government</c> are preserved because the
    /// commander-of-a-state pattern is real (e.g. Empire Day commander = Emperor).
    /// </summary>
    static readonly HashSet<string> ImplausibleCommanderTargets = new(StringComparer.OrdinalIgnoreCase) { "Religion", KgNodeTypes.CelestialBody, KgNodeTypes.Species, KgNodeTypes.Family, "Company" };

    /// <summary>
    /// Walk <paramref name="edges"/>, stamp <c>Meta.SideIndex</c> on every edge
    /// originating from a numbered side field, and drop commander edges with
    /// implausible target types.
    /// </summary>
    public static void StampSideIndex(NodeBuilderContext ctx, List<RelationshipEdge> edges)
    {
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            var edge = edges[i];
            var sourceField = edge.Meta?.SourceFieldLabel;
            if (string.IsNullOrEmpty(sourceField))
                continue;

            var match = SideSuffix.Match(sourceField);
            if (!match.Success)
                continue;

            var prefix = match.Groups[1].Value;
            var side = int.Parse(match.Groups[2].Value);

            // Drop commander edges with implausible target types (per the survey).
            if (string.Equals(prefix, "commanders", StringComparison.OrdinalIgnoreCase))
            {
                var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
                if (ImplausibleCommanderTargets.Contains(targetType))
                {
                    edges.RemoveAt(i);
                    continue;
                }
            }

            edge.Meta ??= new EdgeMeta();
            edge.Meta.SideIndex = side;
        }
    }
}
