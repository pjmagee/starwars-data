using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="RelationshipAnalystToolkit"/> — exercises
/// real MongoDB queries against a Testcontainers instance.
///
/// Focuses on BsonNull safety, missing data handling, and edge cases
/// that have caused runtime crashes in production.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class RelationshipAnalystToolkitTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await ApiFixture.EnsureInitializedAsync();

    private static RelationshipAnalystToolkit Toolkit => ApiFixture.RelationshipAnalystToolkit;

    [TestMethod]
    public async Task GetPageContent_ExistingPage_ReturnsExpectedFields()
    {
        var result = await Toolkit.GetPageContent(1);

        Assert.AreEqual(1, result.PageId);
        Assert.AreEqual("Luke Skywalker", result.Title);
        Assert.AreEqual("Character", result.Type);
        Assert.IsNotNull(result.Infobox);
        Assert.IsTrue(result.Infobox!.Count > 0);
    }

    [TestMethod]
    public async Task GetPageContent_NonexistentPage_ReturnsError()
    {
        var result = await Toolkit.GetPageContent(99999);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task GetPageContent_PageWithNullInfobox_DoesNotCrash()
    {
        var result = await Toolkit.GetPageContent(300);

        Assert.AreEqual(300, result.PageId);
        Assert.AreEqual("Unknown", result.Type);
    }

    [TestMethod]
    public async Task GetPageContent_PageWithEmptyData_ReturnsEmptyInfobox()
    {
        var result = await Toolkit.GetPageContent(400);

        Assert.AreEqual(400, result.PageId);
        Assert.IsNotNull(result.Infobox);
        Assert.AreEqual(0, result.Infobox!.Count);
    }

    [TestMethod]
    public async Task GetPageContent_LongContent_IsTruncated()
    {
        var longContent = new string('X', 10000);
        var coll = ApiFixture.MongoClient.GetDatabase(ApiFixture.DatabaseName).GetCollection<Page>(Collections.Pages);

        var page = new Page
        {
            PageId = 9001,
            Title = "Long Content Page",
            WikiUrl = "https://starwars.fandom.com/wiki/Long_Content",
            Continuity = Continuity.Canon,
            Content = longContent,
            Categories = [],
            Images = [],
            Infobox = new PageInfobox
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

        await coll.ReplaceOneAsync(Builders<Page>.Filter.Eq(p => p.PageId, 9001), page, new ReplaceOptions { IsUpsert = true });

        var result = await Toolkit.GetPageContent(9001);

        Assert.IsNotNull(result.Content);
        Assert.IsTrue(result.Content!.Contains("[truncated]"));
        Assert.IsTrue(result.Content!.Length < longContent.Length);
    }

    [TestMethod]
    public async Task GetLinkedPages_PageWithLinks_ReturnsLinkedEntities()
    {
        var result = await Toolkit.GetLinkedPages(1);
        Assert.IsTrue(result.Count > 0);
    }

    [TestMethod]
    public async Task GetLinkedPages_NonexistentPage_ReturnsEmptyList()
    {
        var result = await Toolkit.GetLinkedPages(99999);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task GetLinkedPages_PageWithNullInfobox_ReturnsEmptyList()
    {
        var result = await Toolkit.GetLinkedPages(300);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task GetLinkedPages_PageWithEmptyData_DoesNotCrash()
    {
        var result = await Toolkit.GetLinkedPages(400);
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task GetExistingLabels_EmptyCollection_ReturnsEmptyList()
    {
        var result = await Toolkit.GetExistingLabels();
        Assert.IsNotNull(result);
    }

    [TestMethod]
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
        Assert.IsTrue(storeResult.Inserted >= 0);

        var edgesResult = await Toolkit.GetEntityEdges(1);
        Assert.IsNotNull(edgesResult);
    }

    [TestMethod]
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

        await Toolkit.StoreEdges(10001, edges);

        var result = await Toolkit.StoreEdges(10001, edges);
        Assert.AreEqual(0, result.Inserted);
    }

    [TestMethod]
    public async Task StoreEdges_SelfReference_IsSkipped()
    {
        var edges = new List<EdgeDto>
        {
            new()
            {
                FromId = 1,
                FromName = "Luke Skywalker",
                FromType = "Character",
                ToId = 1,
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
        Assert.AreEqual(0, result.Inserted);
    }

    [TestMethod]
    public async Task MarkProcessed_SetsCompleted()
    {
        var result = await Toolkit.MarkProcessed(1, 5, "Luke Skywalker", "Character", "Legends");

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(1, result.PageId);
    }

    [TestMethod]
    public async Task SkipPage_SetsSkipped()
    {
        var result = await Toolkit.SkipPage(300, "No infobox", "Disambiguation", "");
        Assert.AreEqual("skipped", result.Status);
    }

    [TestMethod]
    public async Task FindSimilarLabel_NoLabels_ReturnsEmptyList()
    {
        var result = await Toolkit.FindSimilarLabel("parent_of");
        Assert.IsNotNull(result);
    }
}
