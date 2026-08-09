using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Design-032 — <see cref="EventsAtLocationService"/> against a real MongoDB
/// container. Uses a class-scoped database name on the shared
/// <see cref="MongoContainerFixture"/> so seeded kg.* docs can't pollute the
/// shared <see cref="ApiFixture"/> dataset.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class EventsAtLocationServiceTests
{
    private const string Db = "test-starwars-events-at-loc";
    private const int Yavin4 = 100; // a CelestialBody we attach events to

    private static IMongoClient _client = null!;
    private static EventsAtLocationService _service = null!;

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        await MongoContainerFixture.EnsureInitializedAsync();
        _client = MongoContainerFixture.Client;

        var nodes = MongoContainerFixture.GetDatabase(Db).GetCollection<GraphNode>(Collections.KgNodes);
        var edges = MongoContainerFixture.GetDatabase(Db).GetCollection<RelationshipEdge>(Collections.KgEdges);

        await nodes.InsertManyAsync(
            [
                MongoContainerFixture.Node(Yavin4, "Yavin 4", KgNodeTypes.CelestialBody),
                MongoContainerFixture.Node(1, "Battle of Yavin", KgNodeTypes.Battle, "https://wiki/Battle_of_Yavin"),
                MongoContainerFixture.Node(2, "Evacuation of Yavin 4", KgNodeTypes.Mission),
                MongoContainerFixture.Node(3, "Duel on Yavin 4", KgNodeTypes.Duel),
                MongoContainerFixture.Node(4, "Legends Skirmish", KgNodeTypes.Battle, continuity: Continuity.Legends),
                MongoContainerFixture.Node(5, "Jedi Praxeum", KgNodeTypes.Structure), // non-event source
                MongoContainerFixture.Node(6, "Undated Raid", KgNodeTypes.Battle),
                MongoContainerFixture.Node(200, "Yavin system", KgNodeTypes.System),
            ]
        );

        await edges.InsertManyAsync(
            [
                // Battle of Yavin reaches Yavin 4 via TWO labels — must collapse to one row.
                Edge(1, Yavin4, "happened_at", KgNodeTypes.Battle, "Battle of Yavin", year: 0),
                Edge(1, Yavin4, "took_place_at", KgNodeTypes.Battle, "Battle of Yavin", year: 0),
                Edge(2, Yavin4, "took_place_at", KgNodeTypes.Mission, "Evacuation of Yavin 4", year: 4),
                Edge(3, Yavin4, "took_place_at", KgNodeTypes.Duel, "Duel on Yavin 4", year: -3700),
                Edge(4, Yavin4, "took_place_at", KgNodeTypes.Battle, "Legends Skirmish", year: 5, continuity: Continuity.Legends),
                // Structure → located_at: spatial label but NOT an event source — excluded.
                Edge(5, Yavin4, "located_at", KgNodeTypes.Structure, "Jedi Praxeum", year: 0),
                // Battle but a non-spatial label — excluded.
                Edge(6, Yavin4, "participated_in", KgNodeTypes.Battle, "Undated Raid", year: null),
                // Undated event with a valid spatial label — included, sorts last.
                Edge(6, Yavin4, "took_place_at", KgNodeTypes.Battle, "Undated Raid", year: null),
                // Containment: Yavin 4 sits in the Yavin system (200). Drives the
                // Phase 2 roll-up — querying the system surfaces the planet's events.
                Edge(Yavin4, 200, "in_system", KgNodeTypes.CelestialBody, "Yavin 4", year: null),
            ]
        );

        _service = new EventsAtLocationService(NullLogger<EventsAtLocationService>.Instance, Options.Create(new SettingsOptions { DatabaseName = Db }), _client);
    }

    [TestMethod]
    public async Task ReturnsNull_ForUnknownPageId() => Assert.IsNull(await _service.GetEventsAtAsync(999999));

    [TestMethod]
    public async Task CollapsesDuplicateEdges_AndPrefersTookPlaceAt()
    {
        var r = await _service.GetEventsAtAsync(Yavin4);
        Assert.IsNotNull(r);

        var boy = r!.Events.Where(e => e.PageId == 1).ToList();
        Assert.AreEqual(1, boy.Count, "Battle of Yavin must appear once despite two spatial edges");
        Assert.AreEqual("took_place_at", boy[0].EdgeLabel, "the more-specific label wins");
        Assert.AreEqual("Battle", boy[0].Category);
        Assert.AreEqual("https://wiki/Battle_of_Yavin", boy[0].WikiUrl);
    }

    [TestMethod]
    public async Task ExcludesNonEventSourcesAndNonSpatialLabels()
    {
        var r = await _service.GetEventsAtAsync(Yavin4);
        Assert.IsFalse(r!.Events.Any(e => e.PageId == 5), "Structure located_at is not an event");
        // pageId 6 only qualifies through its took_place_at edge, never participated_in.
        Assert.IsTrue(r.Events.Any(e => e.PageId == 6 && e.EdgeLabel == "took_place_at"));
    }

    [TestMethod]
    public async Task SortsByYearBbyToAby_NullsLast()
    {
        var r = await _service.GetEventsAtAsync(Yavin4);
        var ids = r!.Events.Select(e => e.PageId).ToList();

        // -3700 (Duel) < 0 (Battle of Yavin) < 4 (Evac) < 5 (Legends) < null (Undated Raid)
        Assert.AreEqual(3, ids[0]);
        Assert.AreEqual(1, ids[1]);
        Assert.AreEqual(2, ids[2]);
        Assert.AreEqual(6, ids[^1]);
        Assert.AreEqual("Unknown", r.Events.First(e => e.PageId == 6).YearDisplay);
        Assert.AreEqual("0 BBY/ABY", r.Events.First(e => e.PageId == 1).YearDisplay);
        Assert.AreEqual("3700 BBY", r.Events.First(e => e.PageId == 3).YearDisplay);
        Assert.AreEqual("4 ABY", r.Events.First(e => e.PageId == 2).YearDisplay);
    }

    [TestMethod]
    public async Task ContinuityFilter_HidesLegends()
    {
        var canon = await _service.GetEventsAtAsync(Yavin4, Continuity.Canon);
        Assert.IsFalse(canon!.Events.Any(e => e.PageId == 4), "Legends event hidden under Canon filter");

        var both = await _service.GetEventsAtAsync(Yavin4);
        Assert.IsTrue(both!.Events.Any(e => e.PageId == 4), "no filter shows Legends");
    }

    [TestMethod]
    public async Task DirectLocation_HasLocationScope_NoRollUp()
    {
        var r = await _service.GetEventsAtAsync(Yavin4);
        Assert.AreEqual("location", r!.Scope);
        Assert.AreEqual(0, r.RolledUpLocations);
    }

    [TestMethod]
    public async Task System_RollsUpItsWorldsEvents()
    {
        var r = await _service.GetEventsAtAsync(200);
        Assert.IsNotNull(r);
        Assert.AreEqual("system", r!.Scope);
        Assert.AreEqual(1, r.RolledUpLocations, "Yavin 4 folded into the system");
        // The system node itself has no spatial-event edges, but its world does —
        // the roll-up must surface Battle of Yavin et al.
        Assert.IsTrue(r.Events.Any(e => e.PageId == 1), "Battle of Yavin reached via Yavin 4");
        Assert.IsTrue(r.Events.Any(e => e.PageId == 2), "Evacuation of Yavin 4 reached via roll-up");
    }

    [TestMethod]
    public async Task System_RollUp_StillHonoursContinuityFilter()
    {
        var canon = await _service.GetEventsAtAsync(200, Continuity.Canon);
        Assert.IsFalse(canon!.Events.Any(e => e.PageId == 4), "Legends Skirmish hidden under Canon even via roll-up");
    }

    private static RelationshipEdge Edge(int fromId, int toId, string label, string fromType, string fromName, int? year, Continuity continuity = Continuity.Canon) =>
        new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            FromId = fromId,
            FromName = fromName,
            FromType = fromType,
            ToId = toId,
            ToName = "Yavin 4",
            ToType = KgNodeTypes.CelestialBody,
            Label = label,
            Continuity = continuity,
            FromRealm = Realm.Starwars,
            ToRealm = Realm.Starwars,
            FromYear = year,
        };
}
