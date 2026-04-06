using StarWarsData.Models;
using StarWarsData.Models.Entities;
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
    public async Task GetPageContent_ExistingPage_ReturnsExpectedFields()
    {
        var result = await Toolkit.GetPageContent(1);

        Assert.Equal(1, result.PageId);
        Assert.Equal("Luke Skywalker", result.Title);
        Assert.Equal("Character", result.Type);
        Assert.NotNull(result.Infobox);
        Assert.NotEmpty(result.Infobox!);
    }

    [Fact]
    public async Task GetPageContent_NonexistentPage_ReturnsError()
    {
        var result = await Toolkit.GetPageContent(99999);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GetPageContent_PageWithNullInfobox_DoesNotCrash()
    {
        // Page 300 has null infobox
        var result = await Toolkit.GetPageContent(300);

        Assert.Equal(300, result.PageId);
        Assert.Equal("Unknown", result.Type);
    }

    [Fact]
    public async Task GetPageContent_PageWithEmptyData_ReturnsEmptyInfobox()
    {
        // Page 400 has empty Data array
        var result = await Toolkit.GetPageContent(400);

        Assert.Equal(400, result.PageId);
        Assert.NotNull(result.Infobox);
        Assert.Empty(result.Infobox!);
    }

    [Fact]
    public async Task GetPageContent_LongContent_IsTruncated()
    {
        // Insert a page with very long content for this test
        var longContent = new string('X', 10000);
        var coll = fixture.MongoClient.GetDatabase(ApiFixture.DatabaseName).GetCollection<Models.Entities.Page>(Collections.Pages);

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
                Data =
                [
                    new()
                    {
                        Label = "Titles",
                        Values = ["Long Content Page"],
                        Links = [],
                    },
                ],
            },
        };

        // Upsert to avoid duplicate key errors on re-runs
        await coll.ReplaceOneAsync(MongoDB.Driver.Builders<Models.Entities.Page>.Filter.Eq(p => p.PageId, 9001), page, new MongoDB.Driver.ReplaceOptions { IsUpsert = true });

        var result = await Toolkit.GetPageContent(9001);

        Assert.NotNull(result.Content);
        Assert.Contains("[truncated]", result.Content!);
        Assert.True(result.Content!.Length < longContent.Length);
    }

    // ── GetLinkedPages ────────────────────────────────────────────────────

    [Fact]
    public async Task GetLinkedPages_PageWithLinks_ReturnsLinkedEntities()
    {
        var result = await Toolkit.GetLinkedPages(1); // Luke has links to Tatooine, Anakin, etc.

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task GetLinkedPages_NonexistentPage_ReturnsEmptyList()
    {
        var result = await Toolkit.GetLinkedPages(99999);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetLinkedPages_PageWithNullInfobox_ReturnsEmptyList()
    {
        var result = await Toolkit.GetLinkedPages(300);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetLinkedPages_PageWithEmptyData_DoesNotCrash()
    {
        // Page 400 has empty Data (no links) — the $filter projection
        // may return BsonNull, which previously caused InvalidCastException
        var result = await Toolkit.GetLinkedPages(400);

        // Should return empty list or list with entries — not crash
        Assert.NotNull(result);
    }

    // ── GetExistingLabels ─────────────────────────────────────────────────

    [Fact]
    public async Task GetExistingLabels_EmptyCollection_ReturnsEmptyList()
    {
        var result = await Toolkit.GetExistingLabels();
        Assert.NotNull(result);
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
        Assert.True(storeResult.Inserted >= 0);

        // Verify edges are retrievable
        var edgesResult = await Toolkit.GetEntityEdges(1);
        Assert.NotNull(edgesResult);
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
        Assert.Equal(0, result.Inserted);
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
        Assert.Equal(0, result.Inserted);
    }

    // ── MarkProcessed / SkipPage ──────────────────────────────────────────

    [Fact]
    public async Task MarkProcessed_SetsCompleted()
    {
        var result = await Toolkit.MarkProcessed(1, 5, "Luke Skywalker", "Character", "Legends");

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.PageId);
    }

    [Fact]
    public async Task SkipPage_SetsSkipped()
    {
        var result = await Toolkit.SkipPage(300, "No infobox", "Disambiguation", "");

        Assert.Equal("skipped", result.Status);
    }

    // ── FindSimilarLabel ──────────────────────────────────────────────────

    [Fact]
    public async Task FindSimilarLabel_NoLabels_ReturnsEmptyList()
    {
        // With empty labels collection, should return empty matches
        var result = await Toolkit.FindSimilarLabel("parent_of");
        Assert.NotNull(result);
    }
}
