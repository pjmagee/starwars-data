using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.AI.Agents.Holocron.Tools;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Integration coverage for the Mongo-backed surface of <see cref="HolocronReadToolkit"/>.
/// The unit tier (<c>HolocronToolsTests</c>) already covers <c>find_canonical_label</c>
/// and <c>get_template_schema</c>, which don't touch Mongo. This class targets
/// <c>resolve_entity</c> and <c>check_existing_edges</c>, both of which read kg.nodes
/// / kg.edges / kg.edge_enrichments — they need real collections seeded with
/// representative documents.
///
/// Owns a dedicated testcontainer (and hence a dedicated database) so seed writes
/// can't pollute <see cref="ApiFixture"/>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class HolocronReadToolkitTests
{
    const string TestDatabaseName = "test-starwars-holocron-tools";

    static MongoDbContainer _container = null!;
    static IMongoClient _mongoClient = null!;
    static HolocronReadToolkit _toolkit = null!;

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        _container = new MongoDbBuilder("mongo:8").Build();
        await _container.StartAsync();
        _mongoClient = new MongoClient(_container.GetConnectionString());

        await SeedNodesAsync();
        await SeedEdgesAsync();

        _toolkit = new HolocronReadToolkit(_mongoClient, TestDatabaseName);
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    static IMongoCollection<GraphNode> Nodes => _mongoClient.GetDatabase(TestDatabaseName).GetCollection<GraphNode>(Collections.KgNodes);

    static IMongoCollection<RelationshipEdge> Edges => _mongoClient.GetDatabase(TestDatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    static IMongoCollection<EdgeEnrichment> EdgeEnrichments => _mongoClient.GetDatabase(TestDatabaseName).GetCollection<EdgeEnrichment>(Collections.KgEdgeEnrichments);

    static async Task SeedNodesAsync()
    {
        await Nodes.InsertManyAsync(
            new[]
            {
                new GraphNode
                {
                    PageId = 1,
                    Name = "Luke Skywalker",
                    Type = "Character",
                    WikiUrl = "https://starwars.fandom.com/wiki/Luke_Skywalker",
                    Continuity = Continuity.Canon,
                },
                new GraphNode
                {
                    PageId = 2,
                    Name = "Anakin Skywalker",
                    Type = "Character",
                    WikiUrl = "https://starwars.fandom.com/wiki/Anakin_Skywalker",
                    Continuity = Continuity.Canon,
                },
                new GraphNode
                {
                    PageId = 3,
                    Name = "Leia Organa Solo",
                    Type = "Character",
                    WikiUrl = "https://starwars.fandom.com/wiki/Leia_Organa_Solo",
                    Continuity = Continuity.Canon,
                },
                new GraphNode
                {
                    PageId = 100,
                    Name = "Bounty hunter",
                    Type = "TitleOrPosition",
                    WikiUrl = "https://starwars.fandom.com/wiki/Bounty_hunter",
                    Continuity = Continuity.Canon,
                },
                new GraphNode
                {
                    PageId = 200,
                    Name = "Jedi Order",
                    Type = "Organization",
                    WikiUrl = "https://starwars.fandom.com/wiki/Jedi_Order",
                    Continuity = Continuity.Canon,
                },
            }
        );
    }

    static async Task SeedEdgesAsync()
    {
        await Edges.InsertManyAsync(
            new[]
            {
                // Luke parent_of edge — stored Anakin → Luke (parent direction).
                new RelationshipEdge
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    FromId = 2,
                    FromName = "Anakin Skywalker",
                    FromType = "Character",
                    ToId = 1,
                    ToName = "Luke Skywalker",
                    ToType = "Character",
                    Label = "parent_of",
                    ReverseLabel = "child_of",
                    Weight = 1.0,
                    Evidence = "infobox",
                    SourcePageId = 2,
                    Continuity = Continuity.Canon,
                },
                // Luke sibling_of Leia.
                new RelationshipEdge
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    FromId = 1,
                    FromName = "Luke Skywalker",
                    FromType = "Character",
                    ToId = 3,
                    ToName = "Leia Organa Solo",
                    ToType = "Character",
                    Label = "sibling_of",
                    ReverseLabel = "sibling_of",
                    Weight = 1.0,
                    Evidence = "infobox",
                    SourcePageId = 1,
                    Continuity = Continuity.Canon,
                    FromYear = -19,
                },
            }
        );
    }

    // ── ResolveEntity ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveEntity_ExactNameMatch_ReturnsNode()
    {
        var match = await _toolkit.ResolveEntity("Luke Skywalker");
        Assert.IsNotNull(match);
        Assert.AreEqual(1, match.PageId);
        Assert.AreEqual("Luke Skywalker", match.Name);
        Assert.AreEqual("Character", match.Type);
    }

    [TestMethod]
    public async Task ResolveEntity_CaseInsensitive_ReturnsNode()
    {
        var match = await _toolkit.ResolveEntity("luke skywalker");
        Assert.IsNotNull(match);
        Assert.AreEqual(1, match.PageId);
    }

    [TestMethod]
    public async Task ResolveEntity_TitleOrPosition_ReturnsNode()
    {
        // "Bounty hunter" resolves to a TitleOrPosition — the agent should encode
        // a Character→Bounty hunter mention as has_role, not Aliases / Occupation.
        var match = await _toolkit.ResolveEntity("Bounty hunter");
        Assert.IsNotNull(match);
        Assert.AreEqual(100, match.PageId);
        Assert.AreEqual("TitleOrPosition", match.Type);
    }

    [TestMethod]
    public async Task ResolveEntity_NoMatch_ReturnsNull()
    {
        var match = await _toolkit.ResolveEntity("Definitely not a star wars entity");
        Assert.IsNull(match);
    }

    [TestMethod]
    public async Task ResolveEntity_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.IsNull(await _toolkit.ResolveEntity(string.Empty));
        Assert.IsNull(await _toolkit.ResolveEntity("   "));
    }

    // ── CheckExistingEdges ─────────────────────────────────────────────────

    [TestMethod]
    public async Task CheckExistingEdges_StoredDirection_ReturnsForward()
    {
        // Edge stored as Anakin (2) → Luke (1) parent_of.
        var rows = await _toolkit.CheckExistingEdges(2, 1);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("parent_of", rows[0].Label);
        Assert.AreEqual("forward", rows[0].Direction);
        Assert.AreEqual("kg.edges", rows[0].Source);
    }

    [TestMethod]
    public async Task CheckExistingEdges_QueriedReverse_ReturnsReverseDirection()
    {
        // Caller queries Luke (1) → Anakin (2). Edge is stored as Anakin → Luke.
        // Toolkit must surface it with Direction="reverse" so the agent knows the
        // pair has a connection regardless of which side it was thinking of.
        var rows = await _toolkit.CheckExistingEdges(1, 2);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("parent_of", rows[0].Label);
        Assert.AreEqual("reverse", rows[0].Direction);
    }

    [TestMethod]
    public async Task CheckExistingEdges_NoConnection_ReturnsEmpty()
    {
        // Luke (1) ↔ Bounty hunter (100) — no edge seeded.
        var rows = await _toolkit.CheckExistingEdges(1, 100);
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task CheckExistingEdges_SelfLoopOrInvalidIds_ReturnsEmpty()
    {
        Assert.AreEqual(0, (await _toolkit.CheckExistingEdges(1, 1)).Count);
        Assert.AreEqual(0, (await _toolkit.CheckExistingEdges(0, 1)).Count);
        Assert.AreEqual(0, (await _toolkit.CheckExistingEdges(-5, 1)).Count);
    }

    [TestMethod]
    public async Task CheckExistingEdges_IncludesActiveEnrichment()
    {
        // Seed an Active edge enrichment between Luke (1) and Jedi Order (200).
        // No Phase 1 edge between them, so the only result should be the enrichment.
        var enrichmentId = ObjectId.GenerateNewId().ToString();
        try
        {
            await EdgeEnrichments.InsertOneAsync(
                new EdgeEnrichment
                {
                    Id = enrichmentId,
                    FromId = 1,
                    ToId = 200,
                    Label = "affiliated_with",
                    Operation = EnrichmentOperation.Add,
                    Status = EnrichmentStatus.Active,
                    Value = new BsonDocument { { "fromYear", 19 } },
                    Claim = "Luke joined the Jedi Order during the New Republic era.",
                    ContentHashAtCreation = "test-hash",
                    AgentVersion = "holocron-test",
                    ModelId = "test-model",
                }
            );

            var rows = await _toolkit.CheckExistingEdges(1, 200);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("affiliated_with", rows[0].Label);
            Assert.AreEqual("enrichment", rows[0].Source);
            Assert.AreEqual(19, rows[0].FromYear);
        }
        finally
        {
            await EdgeEnrichments.DeleteOneAsync(Builders<EdgeEnrichment>.Filter.Eq(e => e.Id, enrichmentId));
        }
    }

    [TestMethod]
    public async Task CheckExistingEdges_IgnoresNonActiveEnrichment()
    {
        // Stale / Rejected enrichments must not surface — they're not part of the
        // current graph view and the agent shouldn't act on them.
        var enrichmentId = ObjectId.GenerateNewId().ToString();
        try
        {
            await EdgeEnrichments.InsertOneAsync(
                new EdgeEnrichment
                {
                    Id = enrichmentId,
                    FromId = 1,
                    ToId = 200,
                    Label = "trained_at",
                    Operation = EnrichmentOperation.Add,
                    Status = EnrichmentStatus.Stale,
                    Value = new BsonDocument(),
                    Claim = "stale claim",
                    ContentHashAtCreation = "test-hash",
                    AgentVersion = "holocron-test",
                    ModelId = "test-model",
                }
            );

            var rows = await _toolkit.CheckExistingEdges(1, 200);
            Assert.AreEqual(0, rows.Count);
        }
        finally
        {
            await EdgeEnrichments.DeleteOneAsync(Builders<EdgeEnrichment>.Filter.Eq(e => e.Id, enrichmentId));
        }
    }
}
