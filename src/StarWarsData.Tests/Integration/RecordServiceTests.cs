using StarWarsData.Models.Entities;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="StarWarsData.Services.RecordService"/> — exercises real MongoDB queries
/// against a Testcontainers instance seeded with diverse infobox types.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class RecordServiceTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await ApiFixture.EnsureInitializedAsync();

    private static StarWarsData.Services.RecordService Svc => ApiFixture.RecordService;

    [TestMethod]
    public async Task GetCollectionNames_ReturnsDistinctSanitizedTemplates()
    {
        var names = await Svc.GetCollectionNames(CancellationToken.None);

        Assert.IsTrue(names.Contains("Character"));
        Assert.IsTrue(names.Contains("Planet"));
        Assert.IsTrue(names.Contains("Starship"));
        Assert.IsTrue(names.Contains("Battle"));
    }

    [TestMethod]
    public async Task GetCollectionNames_ExcludesPagesWithoutInfobox()
    {
        var names = await Svc.GetCollectionNames(CancellationToken.None);
        Assert.IsFalse(names.Contains("Unknown"));
    }

    [TestMethod]
    public async Task GetCollectionNames_ResultsAreSorted()
    {
        var names = await Svc.GetCollectionNames(CancellationToken.None);

        var sorted = names.OrderBy(x => x).ToList();
        CollectionAssert.AreEqual(sorted, names.ToList());
    }

    [TestMethod]
    public async Task GetCollectionResult_Character_ReturnsAllCharacterPages()
    {
        var result = await Svc.GetCollectionResult("Character", token: CancellationToken.None);

        Assert.IsTrue(result.Total >= 4);
        foreach (var item in result.Items)
            Assert.IsTrue(item.Template.Contains("Character"));
    }

    [TestMethod]
    public async Task GetCollectionResult_Planet_ReturnsPlanetPages()
    {
        var result = await Svc.GetCollectionResult("Planet", token: CancellationToken.None);

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual("Tatooine", result.Items.First().PageTitle);
    }

    [TestMethod]
    public async Task GetCollectionResult_Pagination_RespectsPageSize()
    {
        var page1 = await Svc.GetCollectionResult("Character", page: 1, pageSize: 2, token: CancellationToken.None);

        Assert.IsTrue(page1.Total >= 4);
        Assert.AreEqual(2, page1.Items.Count());
        Assert.AreEqual(1, page1.Page);
        Assert.AreEqual(2, page1.Size);
    }

    [TestMethod]
    public async Task GetCollectionResult_Page2_ReturnsDifferentItems()
    {
        var page1 = await Svc.GetCollectionResult("Character", page: 1, pageSize: 2, token: CancellationToken.None);
        var page2 = await Svc.GetCollectionResult("Character", page: 2, pageSize: 2, token: CancellationToken.None);

        var page1Ids = page1.Items.Select(i => i.PageId).ToHashSet();
        var page2Ids = page2.Items.Select(i => i.PageId).ToHashSet();

        Assert.AreEqual(0, page1Ids.Intersect(page2Ids).Count());
    }

    [TestMethod]
    public async Task GetCollectionResult_WithSearch_FiltersByTitle()
    {
        var result = await Svc.GetCollectionResult("Character", searchText: "Luke", token: CancellationToken.None);

        Assert.IsTrue(result.Total >= 1);
        Assert.IsTrue(result.Items.Any(i => i.PageTitle == "Luke Skywalker"));
    }

    [TestMethod]
    public async Task GetCollectionResult_NonexistentCategory_ReturnsEmpty()
    {
        var result = await Svc.GetCollectionResult("NonexistentType", token: CancellationToken.None);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(0, result.Items.Count());
    }

    [TestMethod]
    public async Task GetCollectionResult_MapsInfoboxDataCorrectly()
    {
        var result = await Svc.GetCollectionResult("Planet", token: CancellationToken.None);

        var tatooine = result.Items.First();
        Assert.AreEqual(100, tatooine.PageId);
        Assert.AreEqual("Tatooine", tatooine.PageTitle);
        Assert.IsTrue(tatooine.Data.Count > 0);
        Assert.IsTrue(tatooine.Data.Any(d => d.Label == "Region"));
        Assert.IsTrue(tatooine.Data.Any(d => d.Label == "Suns" && d.Values.Contains("2")));
    }

    [TestMethod]
    public async Task GetCollectionResult_EmptyDataArray_DoesNotCrash()
    {
        var result = await Svc.GetCollectionResult("Character", token: CancellationToken.None);

        var unknown = result.Items.FirstOrDefault(i => i.PageId == 400);
        Assert.IsNotNull(unknown);
        Assert.AreEqual(0, unknown.Data.Count);
    }

    [TestMethod]
    public async Task GetPageById_ExistingPage_ReturnsPage()
    {
        var page = await Svc.GetPageById(1, CancellationToken.None);

        Assert.IsNotNull(page);
        Assert.AreEqual(1, page.PageId);
        Assert.AreEqual("Luke Skywalker", page.Title);
        Assert.IsNotNull(page.Infobox);
        Assert.IsTrue(page.Infobox.Data.Any(d => d.Label == "Born"));
    }

    [TestMethod]
    public async Task GetPageById_NonexistentPage_ReturnsNull()
    {
        var page = await Svc.GetPageById(99999, CancellationToken.None);
        Assert.IsNull(page);
    }

    [TestMethod]
    public async Task GetPageById_PageWithNullInfobox_ReturnsPageWithNullInfobox()
    {
        var page = await Svc.GetPageById(300, CancellationToken.None);

        Assert.IsNotNull(page);
        Assert.AreEqual("Disambiguation Page", page.Title);
        Assert.IsNull(page.Infobox);
    }

    [TestMethod]
    public async Task GetPagesByIds_MultipleIds_ReturnsAllMatching()
    {
        var pages = await Svc.GetPagesByIds([1, 2, 100], CancellationToken.None);

        Assert.AreEqual(3, pages.Count);
        Assert.IsTrue(pages.Any(p => p.PageId == 1));
        Assert.IsTrue(pages.Any(p => p.PageId == 2));
        Assert.IsTrue(pages.Any(p => p.PageId == 100));
    }

    [TestMethod]
    public async Task GetPagesByIds_MixOfExistingAndNonexistent_ReturnsOnlyExisting()
    {
        var pages = await Svc.GetPagesByIds([1, 99999], CancellationToken.None);

        Assert.AreEqual(1, pages.Count);
        Assert.AreEqual(1, pages[0].PageId);
    }

    [TestMethod]
    public async Task GetPagesByIds_EmptyArray_ReturnsEmpty()
    {
        var pages = await Svc.GetPagesByIds([], CancellationToken.None);
        Assert.AreEqual(0, pages.Count);
    }

    [TestMethod]
    public async Task GetFilteredCollectionNames_ByContinuity_FiltersCorrectly()
    {
        var canonNames = await Svc.GetFilteredCollectionNames(Continuity.Canon, null, CancellationToken.None);

        Assert.IsTrue(canonNames.Contains("Planet"));
        Assert.IsTrue(canonNames.Contains("Battle"));
        Assert.IsTrue(canonNames.Contains("Starship"));
    }

    [TestMethod]
    public async Task GetFilteredCollectionNames_NoContinuityFilter_ReturnsAll()
    {
        var allNames = await Svc.GetFilteredCollectionNames(null, null, CancellationToken.None);

        Assert.IsTrue(allNames.Contains("Character"));
        Assert.IsTrue(allNames.Contains("Planet"));
        Assert.IsTrue(allNames.Contains("Starship"));
        Assert.IsTrue(allNames.Contains("Battle"));
    }
}
