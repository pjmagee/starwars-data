using StarWarsData.Services.AI.Agents.Holocron;
using StarWarsData.Services.AI.Agents.Holocron.Workflows;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Regression coverage for <see cref="HolocronConsolidatorExecutor.MergeEvidence"/> —
/// the cross-batch evidence dedup all four consolidation branches now route through.
///
/// <para>
/// The node-property (Augment) branch previously used record <c>.Distinct()</c>, which
/// keys on <see cref="HolocronEvidencePayload.Excerpt"/> and
/// <see cref="HolocronEvidencePayload.RelevanceScore"/> as well as
/// <see cref="HolocronEvidencePayload.ChunkId"/>. The same chunk retrieved across
/// batches comes back with a slightly different snippet window and cosine score, so
/// it survived structural equality and rendered as a visible duplicate (Maul
/// <c>Titles</c> augment cited chunk <c>…03b49</c> three times at 0.99 / 0.98 / 0.98).
/// Dedup must key on chunkId alone.
/// </para>
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class HolocronEvidenceMergeTests
{
    [TestMethod]
    public void MergeEvidence_SameChunkId_DifferentExcerptAndScore_CollapsesToOne()
    {
        // Mirrors the Maul Titles augment: one chunk, three retrieval hits with
        // drifting snippet windows and scores.
        List<HolocronEvidencePayload> evidence =
        [
            new(SourcePageId: 0, ChunkId: "69c5edeb3318b209a1e03b49", Excerpt: "Maul is known simply as \"the Shadow\" during the Imperial Era.", RelevanceScore: 0.99),
            new(SourcePageId: 0, ChunkId: "69c5edeb3318b209a1e03b49", Excerpt: "Maul, known simply as the Shadow during the Imperial Era", RelevanceScore: 0.98),
            new(
                SourcePageId: 0,
                ChunkId: "69c5edeb3318b209a1e03b49",
                Excerpt: "Maul, known simply as the Shadow during the Imperial Era, was a Force-sensitive Dathomirian Zabrak male",
                RelevanceScore: 0.98
            ),
        ];

        var merged = HolocronConsolidatorExecutor.MergeEvidence(evidence);

        Assert.AreEqual(1, merged.Count, "Same chunkId across batches must collapse to a single evidence entry.");
        Assert.AreEqual("69c5edeb3318b209a1e03b49", merged[0].ChunkId);
    }

    [TestMethod]
    public void MergeEvidence_DistinctChunkIds_AllPreserved()
    {
        // Mirrors the apprentice_of FillGap: three genuinely different chunks —
        // none should be dropped.
        List<HolocronEvidencePayload> evidence =
        [
            new(SourcePageId: 0, ChunkId: "69c5edeb3318b209a1e03b4a", Excerpt: "In 54 BBY, the boy who would be dubbed Darth", RelevanceScore: 0.93),
            new(SourcePageId: 0, ChunkId: "69c5edeb3318b209a1e03b49", Excerpt: "In 54 BBY, the boy who would be dubbed Darth ...", RelevanceScore: 0.70),
            new(SourcePageId: 0, ChunkId: "69c5edeb3318b209a1e03b4b", Excerpt: "In 54 BBY, the boy who would be dubbed Darth …", RelevanceScore: 0.90),
        ];

        var merged = HolocronConsolidatorExecutor.MergeEvidence(evidence);

        Assert.AreEqual(3, merged.Count, "Distinct chunkIds are independent evidence and must all survive.");
    }

    [TestMethod]
    public void MergeEvidence_ChunklessEvidence_DedupedByPage()
    {
        // Page-level citations (no chunkId) fall back to the page key.
        List<HolocronEvidencePayload> evidence =
        [
            new(SourcePageId: 42, ChunkId: null, Excerpt: "first snippet", RelevanceScore: 0.8),
            new(SourcePageId: 42, ChunkId: null, Excerpt: "different snippet same page", RelevanceScore: 0.6),
            new(SourcePageId: 99, ChunkId: null, Excerpt: "other page", RelevanceScore: 0.5),
        ];

        var merged = HolocronConsolidatorExecutor.MergeEvidence(evidence);

        Assert.AreEqual(2, merged.Count, "Chunkless evidence dedups per sourcePageId.");
        CollectionAssert.AreEquivalent(new[] { 42, 99 }, merged.Select(e => e.SourcePageId ?? 0).ToList());
    }
}
