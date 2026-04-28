using MongoDB.Bson;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Shared helper for ISBN field normalisation across book-shaped node builders
/// (Book / ReferenceBook / ComicBook / MagazineIssue).
///
/// <para>Design-024 Phase C — C3: Wookieepedia's book-style infobox templates emit
/// the primary ISBN with NO field label (the row's <c>Label</c> is the empty
/// string) and a single Link to <c>Special:BookSources/&lt;isbn&gt;</c>. The generic
/// loop's normal pipeline routes that link as a relationship edge — which then
/// gets dropped by the coordinator's unresolved-target filter because BookSources
/// URLs aren't real wiki pages. Net effect: the ISBN data is silently lost.</para>
///
/// <para>Volume per the schema survey: ReferenceBook 98%, Book 92.5%,
/// ComicBook 22.5%, MagazineIssue 12.5%.</para>
///
/// <para>The fix runs in <c>OnFinalize</c>: scan <see cref="NodeBuilderContext.DataItems"/>
/// for any row whose <c>Label</c> is empty, take its <c>Values</c> array, and
/// stamp them onto <c>node.Properties["ISBN"]</c>. Then prune any zero-target
/// "ISBN edge" the generic loop emitted from the same row so it doesn't
/// linger as audit clutter (the coordinator would drop it anyway, but pruning
/// here keeps the per-builder unit tests honest).</para>
/// </summary>
internal static class IsbnNormalizer
{
    /// <summary>
    /// If <paramref name="ctx"/>.DataItems contains any empty-label rows with
    /// non-empty Values, accumulate their values onto <paramref name="node"/>.Properties["ISBN"].
    /// Also drop any edge in <paramref name="edges"/> whose SourceFieldLabel is
    /// the empty string — those are the parsed BookSources URL emissions that
    /// would otherwise be dropped downstream as unresolved.
    /// </summary>
    public static void NormalizeEmptyLabelToIsbn(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        // Pull values from any empty-label row(s) — most pages have just one.
        var isbns = new List<string>();
        foreach (var item in ctx.DataItems)
        {
            if (item is not BsonDocument itemDoc)
                continue;
            if (!itemDoc.Contains(InfoboxBsonFields.Label))
                continue;
            var label = itemDoc[InfoboxBsonFields.Label].AsString;
            if (label is null || label.Length > 0)
                continue;

            if (!itemDoc.Contains(InfoboxBsonFields.Values) || !itemDoc[InfoboxBsonFields.Values].IsBsonArray)
                continue;
            foreach (var v in itemDoc[InfoboxBsonFields.Values].AsBsonArray)
            {
                if (v.IsString && !string.IsNullOrWhiteSpace(v.AsString))
                    isbns.Add(v.AsString.Trim());
            }
        }

        if (isbns.Count > 0 && !node.Properties.ContainsKey("ISBN"))
        {
            node.Properties["ISBN"] = isbns;
        }

        // Drop the orphaned edges produced from the empty-label row — they
        // carry SourceFieldLabel = "" because the generic loop interpreted the
        // BookSources URL as a relationship target. Their ToId will be 0
        // (BookSources URLs aren't real wiki pages) so they'd be dropped
        // anyway, but pruning here removes them from per-builder unit-test
        // results and keeps the in-memory edge buffer tidy.
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrEmpty(edges[i].Meta?.SourceFieldLabel))
                edges.RemoveAt(i);
        }
    }
}
