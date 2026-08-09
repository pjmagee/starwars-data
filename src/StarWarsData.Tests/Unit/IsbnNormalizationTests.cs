using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C3: ISBN normalisation across the four book-shaped
/// builders (Book / ReferenceBook / ComicBook / MagazineIssue via
/// <see cref="DefaultNodeBuilder"/> + <see cref="IsbnNormalizer"/>).
/// Wikipedia's template emits ISBN with no field label (empty string) — the
/// parameterized <c>OnFinalize</c> renames <c>properties[""]</c> to
/// <c>properties["ISBN"]</c> via <see cref="IsbnNormalizer"/>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class IsbnNormalizationTests
{
    static NodeBuilderContext BuildBookCtxWithEmptyLabelIsbn(string nodeType, string isbn = "0345407601")
    {
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Titles" },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { "Test Book" }
                },
            },
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "" },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { isbn }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, isbn }, { InfoboxBsonFields.Href, $"/wiki/Special:BookSources/{isbn}" } },
                    }
                },
            },
        };

        return new NodeBuilderContext(
            PageId: 100,
            Title: "Test Book",
            Type: nodeType,
            Continuity: Continuity.Canon,
            Realm: Realm.Real,
            ContentHash: null,
            WikiUrl: "/wiki/Test_Book",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(nodeType),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            NodeTypeByPageId: new Dictionary<int, string>()
        );
    }

    [TestMethod]
    public void Book_EmptyLabelRow_NormalizedToIsbn()
    {
        var ctx = BuildBookCtxWithEmptyLabelIsbn(KgNodeTypes.Book);
        var result = new DefaultNodeBuilder(KgNodeTypes.Book, IsbnNormalizer.NormalizeEmptyLabelToIsbn).Build(ctx);

        Assert.IsTrue(result.Node.Properties.ContainsKey("ISBN"), "ISBN key must be created from empty-label row.");
        Assert.IsFalse(result.Node.Properties.ContainsKey(""), "Empty-string key must be removed.");
        CollectionAssert.AreEqual(new[] { "0345407601" }, result.Node.Properties["ISBN"]);
    }

    [TestMethod]
    public void ReferenceBook_EmptyLabelRow_NormalizedToIsbn()
    {
        var ctx = BuildBookCtxWithEmptyLabelIsbn(KgNodeTypes.ReferenceBook);
        var result = new DefaultNodeBuilder(KgNodeTypes.ReferenceBook, IsbnNormalizer.NormalizeEmptyLabelToIsbn).Build(ctx);

        Assert.IsTrue(result.Node.Properties.ContainsKey("ISBN"));
        Assert.IsFalse(result.Node.Properties.ContainsKey(""));
    }

    [TestMethod]
    public void ComicBook_EmptyLabelRow_NormalizedToIsbn()
    {
        var ctx = BuildBookCtxWithEmptyLabelIsbn(KgNodeTypes.ComicBook);
        var result = new DefaultNodeBuilder(KgNodeTypes.ComicBook, IsbnNormalizer.NormalizeEmptyLabelToIsbn).Build(ctx);

        Assert.IsTrue(result.Node.Properties.ContainsKey("ISBN"));
        Assert.IsFalse(result.Node.Properties.ContainsKey(""));
    }

    [TestMethod]
    public void MagazineIssue_EmptyLabelRow_NormalizedToIsbn()
    {
        var ctx = BuildBookCtxWithEmptyLabelIsbn(KgNodeTypes.MagazineIssue);
        var result = new DefaultNodeBuilder(KgNodeTypes.MagazineIssue, IsbnNormalizer.NormalizeEmptyLabelToIsbn).Build(ctx);

        Assert.IsTrue(result.Node.Properties.ContainsKey("ISBN"));
        Assert.IsFalse(result.Node.Properties.ContainsKey(""));
    }

    [TestMethod]
    public void NoEmptyLabel_LeavesPropertiesUntouched()
    {
        // Page that doesn't have an empty-label row — normalizer is a no-op.
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Titles" },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { "Test Book" }
                },
            },
        };

        var ctx = new NodeBuilderContext(
            PageId: 100,
            Title: "Test Book",
            Type: KgNodeTypes.Book,
            Continuity: Continuity.Canon,
            Realm: Realm.Real,
            ContentHash: null,
            WikiUrl: "/wiki/Test_Book",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Book),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            NodeTypeByPageId: new Dictionary<int, string>()
        );

        var result = new DefaultNodeBuilder(KgNodeTypes.Book, IsbnNormalizer.NormalizeEmptyLabelToIsbn).Build(ctx);
        Assert.IsFalse(result.Node.Properties.ContainsKey("ISBN"));
        Assert.IsFalse(result.Node.Properties.ContainsKey(""));
    }
}
