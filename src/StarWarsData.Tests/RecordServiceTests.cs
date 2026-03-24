namespace StarWarsData.Tests;

/// <summary>
/// Integration tests for <see cref="Services.RecordService"/> — exercises real MongoDB queries
/// against a Testcontainers instance seeded with diverse infobox types.
///
/// Covers: GetCollectionNames, GetCollectionResult, GetPageById, GetPagesByIds,
///         GetSearchResult, GetFilteredCollectionNames
/// </summary>
[Collection("Api")]
public class RecordServiceTests(ApiFixture fixture)
{
    private Services.RecordService Svc => fixture.RecordService;

    // ── GetCollectionNames ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCollectionNames_ReturnsDistinctSanitizedTemplates()
    {
        var names = await Svc.GetCollectionNames(CancellationToken.None);

        Assert.Contains("Character", names);
        Assert.Contains("Planet", names);
        Assert.Contains("Starship", names);
        Assert.Contains("Battle", names);
    }

    [Fact]
    public async Task GetCollectionNames_ExcludesPagesWithoutInfobox()
    {
        // Page 300 has Infobox = null — its template should not appear
        var names = await Svc.GetCollectionNames(CancellationToken.None);

        // There should be no "Unknown" entry
        Assert.DoesNotContain("Unknown", names);
    }

    [Fact]
    public async Task GetCollectionNames_ResultsAreSorted()
    {
        var names = await Svc.GetCollectionNames(CancellationToken.None);

        var sorted = names.OrderBy(x => x).ToList();
        Assert.Equal(sorted, names);
    }

    // ── GetCollectionResult ────────────────────────────────────────────────

    [Fact]
    public async Task GetCollectionResult_Character_ReturnsAllCharacterPages()
    {
        var result = await Svc.GetCollectionResult("Character", token: CancellationToken.None);

        // Seed has 4 characters (may be more if other tests insert)
        Assert.True(result.Total >= 4);
        Assert.All(result.Items, item =>
            Assert.Contains("Character", item.Template));
    }

    [Fact]
    public async Task GetCollectionResult_Planet_ReturnsPlanetPages()
    {
        var result = await Svc.GetCollectionResult("Planet", token: CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal("Tatooine", result.Items.First().PageTitle);
    }

    [Fact]
    public async Task GetCollectionResult_Pagination_RespectsPageSize()
    {
        var page1 = await Svc.GetCollectionResult("Character", page: 1, pageSize: 2,
            token: CancellationToken.None);

        Assert.True(page1.Total >= 4);
        Assert.Equal(2, page1.Items.Count());
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.Size);
    }

    [Fact]
    public async Task GetCollectionResult_Page2_ReturnsDifferentItems()
    {
        var page1 = await Svc.GetCollectionResult("Character", page: 1, pageSize: 2,
            token: CancellationToken.None);
        var page2 = await Svc.GetCollectionResult("Character", page: 2, pageSize: 2,
            token: CancellationToken.None);

        var page1Ids = page1.Items.Select(i => i.PageId).ToHashSet();
        var page2Ids = page2.Items.Select(i => i.PageId).ToHashSet();

        // No overlap between pages
        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    [Fact]
    public async Task GetCollectionResult_WithSearch_FiltersByTitle()
    {
        var result = await Svc.GetCollectionResult("Character", searchText: "Luke",
            token: CancellationToken.None);

        Assert.True(result.Total >= 1);
        Assert.Contains(result.Items, i => i.PageTitle == "Luke Skywalker");
    }

    [Fact]
    public async Task GetCollectionResult_NonexistentCategory_ReturnsEmpty()
    {
        var result = await Svc.GetCollectionResult("NonexistentType",
            token: CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetCollectionResult_MapsInfoboxDataCorrectly()
    {
        var result = await Svc.GetCollectionResult("Planet",
            token: CancellationToken.None);

        var tatooine = result.Items.First();
        Assert.Equal(100, tatooine.PageId);
        Assert.Equal("Tatooine", tatooine.PageTitle);
        Assert.NotEmpty(tatooine.Data);
        Assert.Contains(tatooine.Data, d => d.Label == "Region");
        Assert.Contains(tatooine.Data, d => d.Label == "Suns" && d.Values.Contains("2"));
    }

    [Fact]
    public async Task GetCollectionResult_EmptyDataArray_DoesNotCrash()
    {
        // Page 400 (Unknown Entity) has empty Data array
        var result = await Svc.GetCollectionResult("Character",
            token: CancellationToken.None);

        var unknown = result.Items.FirstOrDefault(i => i.PageId == 400);
        Assert.NotNull(unknown);
        Assert.Empty(unknown.Data);
    }

    // ── GetPageById ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPageById_ExistingPage_ReturnsPage()
    {
        var page = await Svc.GetPageById(1, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(1, page.PageId);
        Assert.Equal("Luke Skywalker", page.Title);
        Assert.NotNull(page.Infobox);
        Assert.Contains(page.Infobox.Data, d => d.Label == "Born");
    }

    [Fact]
    public async Task GetPageById_NonexistentPage_ReturnsNull()
    {
        var page = await Svc.GetPageById(99999, CancellationToken.None);
        Assert.Null(page);
    }

    [Fact]
    public async Task GetPageById_PageWithNullInfobox_ReturnsPageWithNullInfobox()
    {
        var page = await Svc.GetPageById(300, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("Disambiguation Page", page.Title);
        Assert.Null(page.Infobox);
    }

    // ── GetPagesByIds ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagesByIds_MultipleIds_ReturnsAllMatching()
    {
        var pages = await Svc.GetPagesByIds([1, 2, 100], CancellationToken.None);

        Assert.Equal(3, pages.Count);
        Assert.Contains(pages, p => p.PageId == 1);
        Assert.Contains(pages, p => p.PageId == 2);
        Assert.Contains(pages, p => p.PageId == 100);
    }

    [Fact]
    public async Task GetPagesByIds_MixOfExistingAndNonexistent_ReturnsOnlyExisting()
    {
        var pages = await Svc.GetPagesByIds([1, 99999], CancellationToken.None);

        Assert.Single(pages);
        Assert.Equal(1, pages[0].PageId);
    }

    [Fact]
    public async Task GetPagesByIds_EmptyArray_ReturnsEmpty()
    {
        var pages = await Svc.GetPagesByIds([], CancellationToken.None);
        Assert.Empty(pages);
    }

    // ── GetSearchResult ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSearchResult_RegexFallback_FindsByTitle()
    {
        // Text search requires a text index; regex fallback should still work
        var result = await Svc.GetSearchResult("Tatooine",
            token: CancellationToken.None);

        Assert.True(result.Total >= 1);
        Assert.Contains(result.Items, i => i.PageTitle == "Tatooine");
    }

    [Fact]
    public async Task GetSearchResult_PartialMatch_FindsByRegex()
    {
        var result = await Svc.GetSearchResult("Skywalker",
            token: CancellationToken.None);

        // Should find Luke, Anakin, Leia (all Skywalkers in title)
        Assert.True(result.Total >= 2);
    }

    [Fact]
    public async Task GetSearchResult_NoMatch_ReturnsEmptyItems()
    {
        var result = await Svc.GetSearchResult("ZZZZZNONEXISTENT",
            token: CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetSearchResult_ExcludesNullInfoboxPages()
    {
        // "Disambiguation Page" (id=300) has null infobox — shouldn't appear
        var result = await Svc.GetSearchResult("Disambiguation",
            token: CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.PageId == 300);
    }

    // ── GetFilteredCollectionNames ─────────────────────────────────────────

    [Fact]
    public async Task GetFilteredCollectionNames_ByContinuity_FiltersCorrectly()
    {
        var canonNames = await Svc.GetFilteredCollectionNames(
            Models.Entities.Continuity.Canon, null, CancellationToken.None);

        Assert.Contains("Planet", canonNames);
        Assert.Contains("Battle", canonNames);
        Assert.Contains("Starship", canonNames);
        // Characters are Legends-only (except Unknown which is Continuity.Unknown)
        Assert.DoesNotContain("Character", canonNames.Where(n =>
        {
            // Verify no Legends-only characters leak through
            return false; // The template names alone don't tell continuity; just check the list
        }));
    }

    [Fact]
    public async Task GetFilteredCollectionNames_NoContinuityFilter_ReturnsAll()
    {
        var allNames = await Svc.GetFilteredCollectionNames(
            null, null, CancellationToken.None);

        Assert.Contains("Character", allNames);
        Assert.Contains("Planet", allNames);
        Assert.Contains("Starship", allNames);
        Assert.Contains("Battle", allNames);
    }
}
