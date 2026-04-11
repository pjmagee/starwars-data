using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="ArticleChunkingService"/> against a real MongoDB container.
/// Uses its own dedicated container so a failed chunking write cannot pollute the shared
/// <see cref="ApiFixture"/> seed dataset.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class ArticleChunkingIntegrationTests
{
    private const string TestDatabaseName = "test-starwars-chunking";

    private static MongoDbContainer _container = null!;
    private static IMongoClient _mongoClient = null!;
    private static ArticleChunkingService _service = null!;
    private static FakeEmbeddingGenerator _embedder = null!;

    [ClassInitialize]
    public static async Task ClassSetup(TestContext _)
    {
        _container = new MongoDbBuilder("mongo:8").Build();
        await _container.StartAsync();

        _mongoClient = new MongoClient(_container.GetConnectionString());
        _embedder = new FakeEmbeddingGenerator();

        var settings = Options.Create(new SettingsOptions { DatabaseName = TestDatabaseName });

        _service = new ArticleChunkingService(NullLogger<ArticleChunkingService>.Instance, settings, _mongoClient, _embedder, new OpenAiStatusService(NullLogger<OpenAiStatusService>.Instance));
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static IMongoCollection<BsonDocument> Pages => _mongoClient.GetDatabase(TestDatabaseName).GetCollection<BsonDocument>(Collections.Pages);

    private static IMongoCollection<ArticleChunk> Chunks => _mongoClient.GetDatabase(TestDatabaseName).GetCollection<ArticleChunk>(Collections.SearchChunks);

    private static async Task SeedPage(int id, string title, string template, string content, string continuity = "Canon")
    {
        await Pages.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id },
                { "title", title },
                { "content", content },
                { "continuity", continuity },
                {
                    "infobox",
                    new BsonDocument { { "Template", template } }
                },
            }
        );
    }

    [TestMethod]
    public async Task ProcessAllAsync_CreatesChunksForEligiblePages()
    {
        await SeedPage(
            1,
            "Luke Skywalker",
            "Character",
            "## Biography\n"
                + "Luke Skywalker was a Force-sensitive Human male Jedi Master who was instrumental in the defeat of the Galactic Empire and the redemption of his father, Anakin Skywalker.\n\n"
                + "## Powers and abilities\n"
                + "Luke Skywalker was regarded as one of the most powerful Jedi in galactic history, having trained under both Obi-Wan Kenobi and Grand Master Yoda."
        );

        await _service.ProcessAllAsync();

        var chunks = await Chunks.Find(FilterDefinition<ArticleChunk>.Empty).ToListAsync();
        Assert.IsTrue(chunks.Count >= 2, $"Expected at least 2 chunks but got {chunks.Count}");
        foreach (var c in chunks)
        {
            Assert.AreEqual(1, c.PageId);
            Assert.AreEqual("Luke Skywalker", c.Title);
            Assert.AreEqual("Character", c.Type);
            Assert.IsNotNull(c.Embedding);
            Assert.AreEqual(1536, c.Embedding.Length);
            Assert.IsTrue(c.Embedding.Any(v => v != 0f), "Embedding should not be all zeros");
        }
    }

    [TestMethod]
    public async Task ProcessAllAsync_SkipsPagesWithNullInfobox()
    {
        await Pages.InsertOneAsync(
            new BsonDocument
            {
                { "_id", 99 },
                { "title", "Disambiguation Page" },
                { "content", "Some content here" },
                { "continuity", "Canon" },
                { "infobox", BsonNull.Value },
            }
        );

        await _service.ProcessAllAsync();

        var chunks = await Chunks.Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 99)).ToListAsync();
        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public async Task ProcessAllAsync_SkipsPagesWithEmptyContent()
    {
        await SeedPage(98, "Empty Page", "Character", "");

        await _service.ProcessAllAsync();

        var chunks = await Chunks.Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 98)).ToListAsync();
        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public async Task ProcessAllAsync_SkipsAlreadyChunkedPages()
    {
        await SeedPage(
            10,
            "Yoda",
            "Character",
            "## Biography\nYoda was a legendary Jedi Grand Master who served the Galactic Republic for centuries, training many Jedi including Count Dooku and Luke Skywalker."
        );

        await _service.ProcessAllAsync();
        var countAfterFirst = await Chunks.CountDocumentsAsync(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 10));

        await _service.ProcessAllAsync();
        var countAfterSecond = await Chunks.CountDocumentsAsync(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 10));

        Assert.AreEqual(countAfterFirst, countAfterSecond);
    }

    [TestMethod]
    public async Task ProcessAllAsync_HandlesMultiplePages()
    {
        await SeedPage(
            20,
            "Han Solo",
            "Character",
            "## Early life\nHan Solo was a smuggler from Corellia who became a key figure in the Rebel Alliance and later the Resistance against the First Order."
        );
        await SeedPage(
            21,
            "Tatooine",
            "Planet",
            "## Geography\nTatooine was a sparsely inhabited circumbinary desert planet located in the outer reaches of the galaxy, in the Outer Rim Territories."
        );

        await _service.ProcessAllAsync();

        var hanChunks = await Chunks.CountDocumentsAsync(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 20));
        var tatChunks = await Chunks.CountDocumentsAsync(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 21));

        Assert.IsTrue(hanChunks > 0);
        Assert.IsTrue(tatChunks > 0);
    }

    [TestMethod]
    public async Task ProcessAllAsync_SetsCorrectContinuity()
    {
        await SeedPage(
            30,
            "Revan",
            "Character",
            "## Biography\nRevan was a legendary Jedi Knight who fell to the dark side of the Force, becoming the Sith Lord known as Darth Revan before being redeemed.",
            "Legends"
        );

        await _service.ProcessAllAsync();

        var chunks = await Chunks.Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 30)).ToListAsync();
        foreach (var c in chunks)
            Assert.AreEqual(Continuity.Legends, c.Continuity);
    }

    [TestMethod]
    public async Task ProcessAllAsync_SkipsFailedEmbeddings()
    {
        _embedder.FailOnSubstring = "FAIL_THIS";
        try
        {
            await SeedPage(
                40,
                "Bad Page",
                "Character",
                "## Section\nFAIL_THIS content that should fail embedding because it contains a special marker that triggers the fake embedding generator to throw a token limit error."
            );

            await _service.ProcessAllAsync();

            var chunks = await Chunks.Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 40)).ToListAsync();
            Assert.AreEqual(0, chunks.Count);
        }
        finally
        {
            _embedder.FailOnSubstring = null;
        }
    }

    [TestMethod]
    public async Task GetProgressAsync_ReturnsCorrectCounts()
    {
        await SeedPage(
            50,
            "Obi-Wan Kenobi",
            "Character",
            "## Training\nObi-Wan Kenobi was a legendary Jedi Master who trained under Qui-Gon Jinn and later mentored both Anakin Skywalker and his son Luke Skywalker."
        );
        await SeedPage(
            51,
            "Coruscant",
            "Planet",
            "## Description\nCoruscant was the capital planet of the Galactic Republic and later the Galactic Empire, serving as the political and cultural hub of the galaxy for millennia."
        );

        await _service.ProcessAllAsync();

        var progress = await _service.GetProgressAsync();

        Assert.IsTrue(progress.TotalEligiblePages >= 2);
        Assert.IsTrue(progress.ChunkedPages >= 2);
        Assert.IsTrue(progress.TotalChunks >= 2);
        Assert.IsTrue(progress.AvgChunksPerPage > 0);
    }

    [TestMethod]
    public async Task GetProgressAsync_ByTypeBreakdown_GroupsCorrectly()
    {
        await SeedPage(
            60,
            "Mace Windu",
            "Character",
            "## Biography\nMace Windu was a legendary Jedi Master and senior member of the Jedi High Council during the last years of the Galactic Republic, known for his distinctive purple lightsaber."
        );
        await SeedPage(
            61,
            "Naboo",
            "Planet",
            "## Geography\nNaboo was a bountiful planet in the Mid Rim of the galaxy, known for its lush green landscapes, rolling plains, and underwater cities inhabited by the Gungan species."
        );
        await SeedPage(
            62,
            "Darth Maul",
            "Character",
            "## Biography\nDarth Maul was a Dathomirian Zabrak Sith Lord who served as the first apprentice of Darth Sidious, the Dark Lord of the Sith, during the final decades of the Republic."
        );

        await _service.ProcessAllAsync();

        var progress = await _service.GetProgressAsync();

        var charType = progress.ByType.FirstOrDefault(t => t.Type == "Character");
        var planetType = progress.ByType.FirstOrDefault(t => t.Type == "Planet");

        Assert.IsNotNull(charType);
        Assert.IsNotNull(planetType);
        Assert.IsTrue(charType.Pages >= 2);
        Assert.IsTrue(planetType.Pages >= 1);
    }

    [TestMethod]
    public async Task EnsureIndexesAsync_CreatesIndexesWithoutError()
    {
        await _service.EnsureIndexesAsync();

        var cursor = await Chunks.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        var indexNames = indexes.Select(i => i["name"].AsString).ToList();

        Assert.IsTrue(indexNames.Contains("ix_pageId"));
        Assert.IsTrue(indexNames.Contains("ix_type_continuity"));
    }

    [TestMethod]
    public async Task EnsureIndexesAsync_Idempotent_DoesNotThrowOnSecondCall()
    {
        await _service.EnsureIndexesAsync();
        await _service.EnsureIndexesAsync();
    }
}
