using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests;

/// <summary>
/// Unit tests for the pure text-splitting logic in ArticleChunkingService.
/// No MongoDB or embedding calls needed.
/// </summary>
public class ArticleChunkingTextSplittingTests
{
    // ── SplitByMarkdownHeadings ─────────────────────────────────────────

    [Fact]
    public void SplitByMarkdownHeadings_SingleSection_ReturnsContentWithIntroHeading()
    {
        var content =
            "Just some plain text without any headings. This is long enough to be a valid section with meaningful content for chunking.";
        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.Single(sections);
        // Content before any heading gets labelled "Introduction" if there are no headings at all
        // it falls through to the empty-heading fallback
        Assert.Contains("Just some plain text", sections[0].text);
    }

    [Fact]
    public void SplitByMarkdownHeadings_IntroAndHeadings_SplitsCorrectly()
    {
        var content = """
            This is the introduction paragraph.

            ## Biography
            Luke was born on Tatooine.

            ## Powers and abilities
            Luke was a powerful Jedi.

            ### Lightsaber combat
            He was skilled with a lightsaber.
            """;

        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.True(sections.Count >= 4);
        Assert.Equal("Introduction", sections[0].heading);
        Assert.Equal("Biography", sections[1].heading);
        Assert.Equal("Powers and abilities", sections[2].heading);
        Assert.Equal("Lightsaber combat", sections[3].heading);
    }

    [Fact]
    public void SplitByMarkdownHeadings_NoIntro_StartsWithFirstHeading()
    {
        var content = """
            ## Early life
            Born on Tatooine.

            ## Later life
            Became a Jedi.
            """;

        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.True(sections.Count >= 2);
        Assert.Equal("Early life", sections[0].heading);
    }

    [Fact]
    public void SplitByMarkdownHeadings_EmptyContent_ReturnsEmpty()
    {
        var sections = ArticleChunkingService.SplitByMarkdownHeadings("");
        Assert.Empty(sections);
    }

    [Fact]
    public void SplitByMarkdownHeadings_WhitespaceOnly_ReturnsEmpty()
    {
        var sections = ArticleChunkingService.SplitByMarkdownHeadings("   \n\n  ");
        Assert.Empty(sections);
    }

    [Fact]
    public void SplitByMarkdownHeadings_H1NotSplit_TreatedAsContent()
    {
        // Only ## ### #### are split boundaries, not #
        var content = "# Title\nSome content here.";
        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        // Should not split on single #
        Assert.Single(sections);
    }

    // ── SplitByParagraph ────────────────────────────────────────────────

    [Fact]
    public void SplitByParagraph_ShortText_BelowMinChunkChars_ReturnsEmpty()
    {
        // Text below MinChunkChars (100) is filtered out
        var text = "A short paragraph.";
        var chunks = ArticleChunkingService.SplitByParagraph(text);
        Assert.Empty(chunks);
    }

    [Fact]
    public void SplitByParagraph_MediumText_ReturnsSingleChunk()
    {
        var text =
            "A paragraph that is long enough to pass the minimum chunk size threshold. "
            + "It needs to be at least 100 characters to be kept as a valid chunk for embedding.";
        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void SplitByParagraph_LargeText_SplitsIntoMultipleChunks()
    {
        // Build text with many paragraphs that exceed MaxChunkChars (6000)
        var paragraphs = Enumerable
            .Range(1, 30)
            .Select(i => $"Paragraph {i}: " + new string('x', 400))
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
        Assert.All(
            chunks,
            c => Assert.True(c.Length >= 100, "Chunk should be above MinChunkChars")
        );
    }

    [Fact]
    public void SplitByParagraph_NoParagraphBreaks_ReturnsSingleChunk()
    {
        var text = new string('x', 8000); // Large but no paragraph breaks
        var chunks = ArticleChunkingService.SplitByParagraph(text);

        // With no \n\n boundaries, it becomes one chunk
        Assert.Single(chunks);
    }

    [Fact]
    public void SplitByParagraph_SmallParagraphs_AreAggregated()
    {
        // Each paragraph is small, but together they exceed MinChunkChars
        var paragraphs = Enumerable
            .Range(1, 10)
            .Select(i =>
                $"This is paragraph number {i} with enough content to matter for the chunking process."
            )
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var chunks = ArticleChunkingService.SplitByParagraph(text);

        // All small paragraphs should be aggregated into one chunk (total < MaxChunkChars)
        Assert.Single(chunks);
        Assert.All(paragraphs, p => Assert.Contains(p, chunks[0]));
    }

    // ── StripBoilerplate ────────────────────────────────────────────────

    [Fact]
    public void StripBoilerplate_RemovesBase64Images()
    {
        var text = "Some text [![alt](data:image/png;base64,abc123)](link) more text";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.DoesNotContain("data:image", result);
        Assert.Contains("Some text", result);
        Assert.Contains("more text", result);
    }

    [Fact]
    public void StripBoilerplate_RemovesDataUris()
    {
        var text = "Before data:image/png;base64,longstringhere After";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.DoesNotContain("data:image", result);
    }

    [Fact]
    public void StripBoilerplate_CollapsesExcessiveNewlines()
    {
        var text = "First paragraph\n\n\n\n\n\nSecond paragraph";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("First paragraph", result);
        Assert.Contains("Second paragraph", result);
    }

    [Fact]
    public void StripBoilerplate_PlainText_Unchanged()
    {
        var text = "Normal article content with no boilerplate.";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.Equal(text, result);
    }
}

/// <summary>
/// Integration tests for ArticleChunkingService against a real MongoDB container.
/// Tests the full pipeline: chunking, embedding, storage, progress tracking.
/// </summary>
public sealed class ArticleChunkingIntegrationTests : IAsyncLifetime
{
    private MongoDbContainer _container = null!;
    private IMongoClient _mongoClient = null!;
    private ArticleChunkingService _service = null!;
    private FakeEmbeddingGenerator _embedder = null!;

    private const string TestDatabaseName = "test-starwars";

    public async Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8").Build();

        await _container.StartAsync();

        _mongoClient = new MongoClient(_container.GetConnectionString());
        _embedder = new FakeEmbeddingGenerator();

        var settings = Options.Create(
            new SettingsOptions { DatabaseName = TestDatabaseName }
        );

        _service = new ArticleChunkingService(
            NullLogger<ArticleChunkingService>.Instance,
            settings,
            _mongoClient,
            _embedder,
            new OpenAiStatusService(NullLogger<OpenAiStatusService>.Instance)
        );
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private IMongoCollection<BsonDocument> Pages =>
        _mongoClient.GetDatabase(TestDatabaseName).GetCollection<BsonDocument>(Collections.Pages);

    private IMongoCollection<ArticleChunk> Chunks =>
        _mongoClient.GetDatabase(TestDatabaseName).GetCollection<ArticleChunk>(Collections.KgChunks);

    private async Task SeedPage(
        int id,
        string title,
        string template,
        string content,
        string continuity = "Canon"
    )
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

    // ── ProcessAllAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAllAsync_CreatesChunksForEligiblePages()
    {
        // Each section must be >100 chars (MinChunkChars) to produce a chunk
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
        Assert.True(chunks.Count >= 2, $"Expected at least 2 chunks but got {chunks.Count}");
        Assert.All(
            chunks,
            c =>
            {
                Assert.Equal(1, c.PageId);
                Assert.Equal("Luke Skywalker", c.Title);
                Assert.Equal("Character", c.Type);
                Assert.NotNull(c.Embedding);
                Assert.Equal(1536, c.Embedding.Length);
                Assert.True(c.Embedding.Any(v => v != 0f), "Embedding should not be all zeros");
            }
        );
    }

    [Fact]
    public async Task ProcessAllAsync_SkipsPagesWithNullInfobox()
    {
        // Page without infobox — should not be chunked
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

        var chunks = await Chunks
            .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 99))
            .ToListAsync();
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ProcessAllAsync_SkipsPagesWithEmptyContent()
    {
        await SeedPage(98, "Empty Page", "Character", "");

        await _service.ProcessAllAsync();

        var chunks = await Chunks
            .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 98))
            .ToListAsync();
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ProcessAllAsync_SkipsAlreadyChunkedPages()
    {
        await SeedPage(
            10,
            "Yoda",
            "Character",
            "## Biography\nYoda was a legendary Jedi Grand Master who served the Galactic Republic for centuries, training many Jedi including Count Dooku and Luke Skywalker."
        );

        // Run twice
        await _service.ProcessAllAsync();
        var countAfterFirst = await Chunks.CountDocumentsAsync(
            Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 10)
        );

        await _service.ProcessAllAsync();
        var countAfterSecond = await Chunks.CountDocumentsAsync(
            Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 10)
        );

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
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

        var hanChunks = await Chunks.CountDocumentsAsync(
            Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 20)
        );
        var tatChunks = await Chunks.CountDocumentsAsync(
            Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 21)
        );

        Assert.True(hanChunks > 0);
        Assert.True(tatChunks > 0);
    }

    [Fact]
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

        var chunks = await Chunks
            .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 30))
            .ToListAsync();
        Assert.All(chunks, c => Assert.Equal(Continuity.Legends, c.Continuity));
    }

    [Fact]
    public async Task ProcessAllAsync_SkipsFailedEmbeddings()
    {
        _embedder.FailOnSubstring = "FAIL_THIS";
        await SeedPage(
            40,
            "Bad Page",
            "Character",
            "## Section\nFAIL_THIS content that should fail embedding because it contains a special marker that triggers the fake embedding generator to throw a token limit error."
        );

        await _service.ProcessAllAsync();

        var chunks = await Chunks
            .Find(Builders<ArticleChunk>.Filter.Eq(c => c.PageId, 40))
            .ToListAsync();
        // The chunk should be skipped (zero vector filtered out)
        Assert.Empty(chunks);
    }

    // ── GetProgressAsync ────────────────────────────────────────────────

    [Fact]
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

        Assert.Equal(2, progress.TotalEligiblePages);
        Assert.Equal(2, progress.ChunkedPages);
        Assert.Equal(0, progress.PendingPages);
        Assert.True(progress.TotalChunks >= 2);
        Assert.True(progress.AvgChunksPerPage > 0);
    }

    [Fact]
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

        Assert.NotNull(charType);
        Assert.NotNull(planetType);
        Assert.Equal(2, charType.Pages);
        Assert.Equal(1, planetType.Pages);
    }

    // ── EnsureIndexesAsync ──────────────────────────────────────────────

    [Fact]
    public async Task EnsureIndexesAsync_CreatesIndexesWithoutError()
    {
        await _service.EnsureIndexesAsync();

        // Verify indexes were created
        var cursor = await Chunks.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        var indexNames = indexes.Select(i => i["name"].AsString).ToList();

        Assert.Contains("ix_pageId", indexNames);
        Assert.Contains("ix_type_continuity", indexNames);
    }

    [Fact]
    public async Task EnsureIndexesAsync_Idempotent_DoesNotThrowOnSecondCall()
    {
        await _service.EnsureIndexesAsync();
        await _service.EnsureIndexesAsync(); // Should not throw
    }
}

/// <summary>
/// Fake embedding generator that returns deterministic vectors for testing.
/// Optionally fails for inputs containing a specific substring.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("fake-embedder");

    /// <summary>If set, any input containing this substring will throw a token limit error.</summary>
    public string? FailOnSubstring { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var embeddings = new List<Embedding<float>>();
        foreach (var value in values)
        {
            if (FailOnSubstring is not null && value.Contains(FailOnSubstring))
                throw new InvalidOperationException(
                    "This model's maximum context length is 8192 tokens, however you requested 99999 tokens (99999 in your prompt; 0 for the completion). Please reduce your prompt; or completion length."
                );

            // Generate a deterministic non-zero vector based on content hash
            var hash = value.GetHashCode();
            var vector = new float[1536];
            var rng = new Random(hash);
            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)(rng.NextDouble() * 2 - 1);
            embeddings.Add(new Embedding<float>(vector));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
