using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.AI.Citations;
using StarWarsData.Services.AI.RequestContext;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Design-030 Phases 2 + 4 — <see cref="CitationResolver"/> against a real
/// MongoDB container. Owns its own container so seeded kg.* docs can't pollute
/// the shared <see cref="Infrastructure.ApiFixture"/> dataset.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class CitationResolverTests
{
    private const string Db = "test-starwars-citations";

    private static MongoDbContainer _container = null!;
    private static IMongoClient _client = null!;
    private static readonly CurrentRequestContext Ctx = new();

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        _container = new MongoDbBuilder("mongo:8").Build();
        await _container.StartAsync();
        _client = new MongoClient(_container.GetConnectionString());
        var db = _client.GetDatabase(Db);

        await db.GetCollection<GraphNode>(Collections.KgNodes)
            .InsertManyAsync(
                [
                    Node(10, "Yavin 4", KgNodeTypes.CelestialBody, "https://wiki/Yavin_4"),
                    Node(11, "Korriban", KgNodeTypes.CelestialBody, continuity: Continuity.Legends),
                    Node(20, "Battle of Yavin", KgNodeTypes.Battle),
                    Node(30, "Luke Skywalker", KgNodeTypes.Character),
                    Node(40, "Han Solo", KgNodeTypes.Character),
                    Node(50, "Boba Fett", KgNodeTypes.Character), // no spatial edge
                ]
            );

        await db.GetCollection<RelationshipEdge>(Collections.KgEdges)
            .InsertManyAsync(
                [
                    Edge(20, 10, "took_place_at", KgNodeTypes.CelestialBody, Continuity.Canon, weight: 5),
                    Edge(30, 10, "homeworld", KgNodeTypes.CelestialBody, Continuity.Canon, weight: 9),
                    // Han Solo → Legends-only location; hidden when the Canon filter is on.
                    Edge(40, 11, "homeworld", KgNodeTypes.CelestialBody, Continuity.Legends, weight: 7),
                ]
            );

        // No timeline / holocron seed: the resolver no longer probes those
        // collections — slim citation card surfaces Node / Location / Wiki only
        // (Design-041 follow-up). Timeline and Holocron remain reachable from
        // the KG node detail page itself.
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static CitationResolver NewResolver()
    {
        Ctx.Continuity = null;
        return new CitationResolver(Options.Create(new SettingsOptions { DatabaseName = Db }), _client, Ctx);
    }

    [TestMethod]
    public async Task DirectSpatialType_GetsOwnGalaxyMapLink()
    {
        var r = (await NewResolver().ResolveAsync([10])).Single();
        Assert.AreEqual("/galaxy-map/10", r.Links.GalaxyMap);
        Assert.AreEqual("https://wiki/Yavin_4", r.Links.Wiki);
        Assert.AreEqual("/knowledge-graph/nodes/10", r.Links.KnowledgeGraph);
    }

    [TestMethod]
    public async Task EventFamilyIndirect_EmitsEventRoundTrip()
    {
        var r = (await NewResolver().ResolveAsync([20])).Single();
        // Battle → took_place_at → Yavin 4, and Battle is Event-family → ?event=
        Assert.AreEqual("/galaxy-map/10?event=20", r.Links.GalaxyMap);
    }

    [TestMethod]
    public async Task NonEventIndirect_LinksToLocationWithoutEventParam()
    {
        var r = (await NewResolver().ResolveAsync([30])).Single();
        Assert.AreEqual("/galaxy-map/10", r.Links.GalaxyMap);
    }

    [TestMethod]
    public async Task KnownNode_AlwaysGetsKnowledgeGraphLink()
    {
        // KG node detail is the canonical surface for any KG entity — it's the
        // jumping-off point to Graph Explorer / Timeline / Holocron, which
        // explains why we no longer emit those as separate chips.
        var luke = (await NewResolver().ResolveAsync([30])).Single();
        Assert.AreEqual("/knowledge-graph/nodes/30", luke.Links.KnowledgeGraph);

        var han = (await NewResolver().ResolveAsync([40])).Single();
        Assert.AreEqual("/knowledge-graph/nodes/40", han.Links.KnowledgeGraph);
    }

    [TestMethod]
    public async Task NoSpatialEdge_NoGalaxyMapLink()
    {
        var r = (await NewResolver().ResolveAsync([50])).Single();
        Assert.IsNull(r.Links.GalaxyMap);
        Assert.AreEqual("/knowledge-graph/nodes/50", r.Links.KnowledgeGraph);
    }

    [TestMethod]
    public async Task ContinuityFilter_SuppressesCrossContinuityIndirectTarget()
    {
        var resolver = NewResolver();
        Ctx.Continuity = Continuity.Canon;
        var r = (await resolver.ResolveAsync([40])).Single(); // Han → Legends homeworld
        Assert.IsNull(r.Links.GalaxyMap, "Canon filter must hide the Legends-only indirect target");

        Ctx.Continuity = null;
        var unfiltered = (await NewResolver().ResolveAsync([40])).Single();
        Assert.AreEqual("/galaxy-map/11", unfiltered.Links.GalaxyMap);
    }

    [TestMethod]
    public async Task UnknownId_ReturnsMinimalReference()
    {
        var r = (await NewResolver().ResolveAsync([999999])).Single();
        Assert.AreEqual("Unknown", r.Kind);
        Assert.IsNull(r.Links.Wiki);
        Assert.IsNull(r.Links.GalaxyMap);
    }

    private static GraphNode Node(int id, string name, string type, string? wikiUrl = null, Continuity continuity = Continuity.Canon) =>
        new()
        {
            PageId = id,
            Name = name,
            Type = type,
            Continuity = continuity,
            Realm = Realm.Starwars,
            WikiUrl = wikiUrl,
        };

    private static RelationshipEdge Edge(int fromId, int toId, string label, string toType, Continuity continuity, double weight) =>
        new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            FromId = fromId,
            ToId = toId,
            ToType = toType,
            Label = label,
            Continuity = continuity,
            Weight = weight,
            FromRealm = Realm.Starwars,
            ToRealm = Realm.Starwars,
        };
}
