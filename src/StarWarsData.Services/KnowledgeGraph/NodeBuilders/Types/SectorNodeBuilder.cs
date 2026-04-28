using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Sector"/> extraction.
///
/// <para>Design-024 Phase B (Pattern C — field-alias collapse): Sector pages
/// use ~150 distinct named fields for "conflict that took place in this sector"
/// (<c>Galactic Civil War</c>, <c>Clone Wars</c>, <c>Yuuzhan Vong War</c>,
/// <c>Great Sith War</c>, <c>Mandalorian Wars</c>, <c>Imperial Period conflicts</c>,
/// …) and ~10 era variants (<c>Rise of the Empire era</c>, <c>Imperial Era</c>,
/// <c>New Republic era</c>, …). Without a per-type override, each becomes its own
/// auto-normalised label (<c>yuuzhan_vong_war</c>, <c>mandalorian_wars</c>, …) and
/// queries asking "all conflicts in this sector" can never enumerate them.</para>
///
/// <para>This <see cref="OnFinalize"/> override walks edges and relabels any
/// edge whose <c>SourceFieldLabel</c> matches a conflict keyword OR an era
/// keyword to <c>has_conflict</c>. Era-named fields turn out to contain
/// Battle / War / Mission links (events that happened during that era), not
/// pointers to <see cref="KgNodeTypes.Era"/> nodes — so <c>has_conflict</c>
/// is the right semantic for both. The original <c>in_era</c> proposal was
/// dropped after a data inspection (Design-024 Phase B note).</para>
///
/// <para>The <c>Sector capital</c>, <c>Subsectors</c>, and <c>Stations</c> fields
/// are picked up via direct <see cref="Definitions.FieldSemantics"/> entries —
/// no override needed.</para>
/// </summary>
public sealed class SectorNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Sector;

    /// <summary>
    /// Whole-word keywords that identify a war-or-conflict field name. Detection
    /// is case-insensitive and uses substring match against word-boundaries —
    /// generous on purpose so future Wookieepedia conflict-named fields are
    /// caught without a code change. False-positive risk is bounded by the small
    /// vocabulary of Sector infobox field names (~150 known names, all listed
    /// in TemplateFields.g.cs).
    /// </summary>
    static readonly string[] ConflictKeywords =
    [
        "War",
        "Wars",
        "Conflict",
        "Conflicts",
        "Crisis",
        "Crusade",
        "Crusades",
        "Insurgency",
        "Insurrection",
        "Revolution",
        "Uprising",
        "Uprisings",
        "Purge",
        "Purges",
        "Invasion",
        "Invasions",
        "Massacres",
        "Massacre",
        "Schism",
        "Hunt",
        "skirmishes",
        "Skirmish",
        "Campaign",
        "Campaigns",
        "Contention",
        "conquests",
        "Reconquest",
        "Disorders",
        "emergence",
        "plot",
        "rebellion",
        "Rebellion",
        "Occupation",
        "expansion",
        "Storm",
        "Matrica",
        "missions",
    ];

    /// <summary>
    /// Whole-word keywords that identify an era-or-period field name on a Sector
    /// page (<c>Rise of the Empire era</c>, <c>Imperial Period</c>, …). These
    /// fields list events from that era — sample inspection shows links resolve
    /// to Battle/War/Mission targets, not Era nodes — so they collapse to
    /// <c>has_conflict</c> alongside the conflict keywords.
    /// </summary>
    static readonly string[] EraKeywords = ["era", "Era", "Period", "Age"];

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        foreach (var edge in edges)
        {
            var sourceField = edge.Meta?.SourceFieldLabel;
            if (string.IsNullOrEmpty(sourceField))
                continue;
            // Skip the canonical fields that already have correct labels via FieldSemantics
            // (Galactic Civil War, Clone Wars, Local conflict, Conflicts → has_conflict).
            if (string.Equals(edge.Label, "has_conflict", StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasWordKeyword(sourceField, ConflictKeywords) || HasWordKeyword(sourceField, EraKeywords))
            {
                edge.Label = "has_conflict";
            }
        }
    }

    /// <summary>
    /// Case-insensitive whole-word keyword match. Splits the field name on
    /// non-letter chars and tests each token against the keyword set.
    /// </summary>
    static bool HasWordKeyword(string fieldName, string[] keywords)
    {
        // Tokenise on whitespace + en/em dash + punctuation.
        var span = fieldName.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            var atEnd = i == span.Length;
            var c = atEnd ? ' ' : span[i];
            var isBoundary = atEnd || !(char.IsLetter(c) || c == '\'');
            if (isBoundary)
            {
                if (i > start)
                {
                    var token = span[start..i];
                    foreach (var kw in keywords)
                    {
                        if (token.Equals(kw, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                start = i + 1;
            }
        }
        return false;
    }
}
