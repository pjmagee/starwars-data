using System.Text.RegularExpressions;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.TelevisionEpisode"/> extraction.
///
/// <para>Design-024 Phase C — C2 covers three rules:</para>
/// <list type="bullet">
///   <item><c>Guest star(s)</c> field (~12.5% of pages, ~376 edges) — promoted
///   from auto-normalised <c>guest_star</c> to canonical <c>featured_actor</c>
///   via direct <see cref="Definitions.FieldSemantics.Relationships"/> entry.
///   The generic loop picks it up automatically.</item>
///   <item><c>Production company</c> field (~31 edges) — promoted to
///   <c>produced_by</c> via direct FieldSemantics entry.</item>
///   <item><c>Timeline  (Canon)</c> / <c>Timeline  (Legends)</c> — the wiki uses
///   continuity-suffixed variants of the canonical <c>Timeline</c> field. The
///   generic loop sees them as distinct labels and emits auto-normalised
///   <c>timeline_canon</c> / <c>timeline_legends</c>. This <see cref="OnFinalize"/>
///   override relabels them to <c>in_timeline</c> (the canonical label
///   <c>Timeline</c> already maps to) so the same downstream queries work
///   uniformly across continuities. Note the wiki uses TWO spaces between
///   "Timeline" and the parenthetical — see TemplateFields.g.cs.</item>
/// </list>
/// </summary>
public sealed class TelevisionEpisodeNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.TelevisionEpisode;

    /// <summary>
    /// Matches "Timeline" optionally followed by a parenthetical continuity
    /// suffix — handles both single- and double-space variants. Tolerant of
    /// trailing whitespace.
    /// </summary>
    static readonly Regex TimelineWithSuffix = new(@"^Timeline\s*\([^)]+\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        foreach (var edge in edges)
        {
            var sourceField = edge.Meta?.SourceFieldLabel;
            if (string.IsNullOrEmpty(sourceField))
                continue;
            if (!TimelineWithSuffix.IsMatch(sourceField))
                continue;
            // Re-route the continuity-suffixed Timeline variants onto the canonical
            // in_timeline label. The generic loop has already auto-normalised them
            // to timeline_canon / timeline_legends via NormaliseLabel — we replace
            // that with the FieldSemantics canonical so downstream queries unify.
            edge.Label = "in_timeline";
        }
    }
}
