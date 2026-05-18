using StarWarsData.Models.Entities;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-032 — the shared spatial-event catalogue. Locks the contract that
/// backend, frontend, and the citation resolver all read from a single place.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class SpatialEventLabelsTests
{
    [TestMethod]
    public void Labels_ContainsTheVerbTheCorpusActuallyUses()
    {
        // took_place_at is the only label events use to reach a place in
        // starwars-dev (probed 2026-05-18) — regressing it would empty every list.
        Assert.IsTrue(SpatialEventLabels.Labels.Contains("took_place_at"));
        Assert.IsTrue(SpatialEventLabels.Labels.Contains("TOOK_PLACE_AT"), "label set must be case-insensitive");
    }

    [TestMethod]
    public void EventFamily_IncludesDuel_WhichTheCorpusUsesWithTookPlaceAt()
    {
        Assert.IsTrue(SpatialEventLabels.EventFamily.Contains(KgNodeTypes.Duel));
        Assert.IsTrue(SpatialEventLabels.EventFamily.Contains(KgNodeTypes.Battle));
        Assert.IsTrue(SpatialEventLabels.EventFamily.Contains("Siege"));
    }

    [TestMethod]
    public void EventFamily_ExcludesNonEventSpatialNeighbours()
    {
        // Structures / Locations / Species reach places via located_at too —
        // they must NOT be treated as events.
        Assert.IsFalse(SpatialEventLabels.EventFamily.Contains(KgNodeTypes.Structure));
        Assert.IsFalse(SpatialEventLabels.EventFamily.Contains(KgNodeTypes.Location));
        Assert.IsFalse(SpatialEventLabels.EventFamily.Contains(KgNodeTypes.Species));
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Battle, "Battle")]
    [DataRow(KgNodeTypes.Mission, "Mission")]
    [DataRow(KgNodeTypes.War, "War")]
    [DataRow(KgNodeTypes.Duel, "Duel")]
    [DataRow("Siege", "Siege")]
    [DataRow("SomethingNew", "Event")]
    [DataRow(null, "Event")]
    public void CategoryFor_BucketsKnownTypes_AndFallsBackToEvent(string? type, string expected) => Assert.AreEqual(expected, SpatialEventLabels.CategoryFor(type));
}
