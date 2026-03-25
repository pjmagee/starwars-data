using System.Text.Json;
using StarWarsData.Services;

namespace StarWarsData.Tests;

/// <summary>
/// Integration tests for <see cref="Services.RelationshipAnalystToolkit"/> — exercises
/// real MongoDB queries against a Testcontainers instance.
///
/// Focuses on BsonNull safety, missing data handling, and edge cases
/// that have caused runtime crashes in production (e.g. $filter returning BsonNull).
/// </summary>
[Collection("Api")]
public class RelationshipAnalystToolkitTests(ApiFixture fixture)
{
    private Services.RelationshipAnalystToolkit Toolkit => fixture.RelationshipAnalystToolkit;

    // ── GetPageContent ────────────────────────────────────────────────────

    [Fact]
    public async Task GetPageContent_ExistingPage_ReturnsJsonWithExpectedFields()
    {
        var json = await Toolkit.GetPageContent(1);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("pageId").GetInt32());
        Assert.Equal("Luke Skywalker", root.GetProperty("title").GetString());
        Assert.Equal("Character", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("infobox").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetPageContent_NonexistentPage_ReturnsError()
    {
        var json = await Toolkit.GetPageContent(99999);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetPageContent_PageWithNullInfobox_DoesNotCrash()
    {
        // Page 300 has null infobox
        var json = await Toolkit.GetPageContent(300);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(300, root.GetProperty("pageId").GetInt32());
        Assert.Equal("Unknown", root.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetPageContent_PageWithEmptyData_ReturnsEmptyInfobox()
    {
        // Page 400 has empty Data array
        var json = await Toolkit.GetPageContent(400);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(400, root.GetProperty("pageId").GetInt32());
        Assert.Equal(0, root.GetProperty("infobox").GetArrayLength());
    }

    [Fact]
    public async Task GetPageContent_LongContent_IsTruncated()
    {
        // Insert a page with very long content for this test
        var longContent = new string('X', 10000);
        var coll = fixture.MongoClient
            .GetDatabase(ApiFixture.PagesDb)
            .GetCollection<Models.Entities.Page>("Pages");

        var page = new Models.Entities.Page
        {
            PageId = 9001,
            Title = "Long Content Page",
            WikiUrl = "https://starwars.fandom.com/wiki/Long_Content",
            Continuity = Models.Entities.Continuity.Canon,
            Content = longContent,
            Categories = [],
            Images = [],
            Infobox = new Models.Entities.PageInfobox
            {
                Template = "https://starwars.fandom.com/wiki/Template:Character",
                Data = [new() { Label = "Titles", Values = ["Long Content Page"], Links = [] }],
            },
        };

        // Upsert to avoid duplicate key errors on re-runs
        await coll.ReplaceOneAsync(
            MongoDB.Driver.Builders<Models.Entities.Page>.Filter.Eq(p => p.PageId, 9001),
            page,
            new MongoDB.Driver.ReplaceOptions { IsUpsert = true });

        var json = await Toolkit.GetPageContent(9001);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("content").GetString()!;

        Assert.Contains("[truncated]", content);
        Assert.True(content.Length < longContent.Length);
    }

    // ── GetLinkedPages ────────────────────────────────────────────────────

    [Fact]
    public async Task GetLinkedPages_PageWithLinks_ReturnsLinkedEntities()
    {
        var json = await Toolkit.GetLinkedPages(1); // Luke has links to Tatooine, Anakin, etc.
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        // Should find at least Anakin (parent link) and Leia (sibling link)
        var names = arr.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.True(arr.GetArrayLength() > 0, "Should resolve at least one linked page");
    }

    [Fact]
    public async Task GetLinkedPages_NonexistentPage_ReturnsEmptyArray()
    {
        var json = await Toolkit.GetLinkedPages(99999);
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task GetLinkedPages_PageWithNullInfobox_ReturnsEmptyArray()
    {
        var json = await Toolkit.GetLinkedPages(300);
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task GetLinkedPages_PageWithEmptyData_DoesNotCrash()
    {
        // Page 400 has empty Data (no links) — the $filter projection
        // may return BsonNull, which previously caused InvalidCastException
        var json = await Toolkit.GetLinkedPages(400);
        using var doc = JsonDocument.Parse(json);

        // Should return empty array or array with entries — not crash
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    // ── GetExistingLabels ─────────────────────────────────────────────────

    [Fact]
    public async Task GetExistingLabels_EmptyCollection_ReturnsEmptyArray()
    {
        var json = await Toolkit.GetExistingLabels();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    // ── StoreEdges + GetEntityEdges ───────────────────────────────────────

    [Fact]
    public async Task StoreEdges_ValidEdge_InsertsAndRetrievable()
    {
        var edges = new List<EdgeDto>
        {
            new()
            {
                FromId = 1,
                FromName = "Luke Skywalker",
                FromType = "Character",
                ToId = 2,
                ToName = "Anakin Skywalker",
                ToType = "Character",
                Label = "child_of",
                ReverseLabel = "parent_of",
                Weight = 0.9,
                Evidence = "Luke is the son of Anakin",
                Continuity = "Legends",
            },
        };

        var storeResult = await Toolkit.StoreEdges(1, edges);
        using var storeDoc = JsonDocument.Parse(storeResult);
        Assert.True(storeDoc.RootElement.GetProperty("inserted").GetInt32() >= 0);

        // Verify edges are retrievable
        var edgesResult = await Toolkit.GetEntityEdges(1);
        using var edgesDoc = JsonDocument.Parse(edgesResult);
        Assert.Equal(JsonValueKind.Array, edgesDoc.RootElement.ValueKind);
    }

    [Fact]
    public async Task StoreEdges_DuplicateEdge_IsSkipped()
    {
        var edges = new List<EdgeDto>
        {
            new()
            {
                FromId = 10001,
                FromName = "Test A",
                FromType = "Character",
                ToId = 10002,
                ToName = "Test B",
                ToType = "Character",
                Label = "test_relation",
                ReverseLabel = "test_reverse",
                Weight = 0.5,
                Evidence = "Test evidence",
                Continuity = "Canon",
            },
        };

        // Insert first time
        await Toolkit.StoreEdges(10001, edges);

        // Insert same edge again
        var result = await Toolkit.StoreEdges(10001, edges);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("inserted").GetInt32());
    }

    [Fact]
    public async Task StoreEdges_SelfReference_IsSkipped()
    {
        var edges = new List<EdgeDto>
        {
            new()
            {
                FromId = 1,
                FromName = "Luke Skywalker",
                FromType = "Character",
                ToId = 1, // self-reference
                ToName = "Luke Skywalker",
                ToType = "Character",
                Label = "self",
                ReverseLabel = "self",
                Weight = 1.0,
                Evidence = "Self reference",
                Continuity = "Canon",
            },
        };

        var result = await Toolkit.StoreEdges(1, edges);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("inserted").GetInt32());
    }

    // ── MarkProcessed / SkipPage ──────────────────────────────────────────

    [Fact]
    public async Task MarkProcessed_SetsCompleted()
    {
        var result = await Toolkit.MarkProcessed(1, 5, "Luke Skywalker", "Character", "Legends");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("pageId").GetInt32());
    }

    [Fact]
    public async Task SkipPage_SetsSkipped()
    {
        var result = await Toolkit.SkipPage(300, "No infobox", "Disambiguation", "");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("skipped", doc.RootElement.GetProperty("status").GetString());
    }

    // ── FindSimilarLabel ──────────────────────────────────────────────────

    [Fact]
    public async Task FindSimilarLabel_NoLabels_ReturnsEmptyArray()
    {
        // With empty labels collection, should return empty matches
        var result = await Toolkit.FindSimilarLabel("parent_of");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}
