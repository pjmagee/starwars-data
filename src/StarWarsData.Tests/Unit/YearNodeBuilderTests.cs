using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B for Year nodes (via <see cref="DefaultNodeBuilder"/>).
/// Verifies <c>Chancellor</c> / <c>Head</c> / <c>Chief</c> are picked up as
/// typed-leader edges via direct FieldSemantics entries (no override needed —
/// the generic loop classifies them).
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class YearNodeBuilderTests
{
    static NodeBuilderContext BuildYearCtx(string fieldLabel, string targetTitle = "Palpatine")
    {
        const int targetPageId = 200;
        var targetUrl = $"/wiki/{targetTitle}";
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, fieldLabel },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { targetTitle }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, targetTitle }, { InfoboxBsonFields.Href, targetUrl } },
                    }
                },
            },
        };

        return new NodeBuilderContext(
            PageId: 100,
            Title: "22 BBY",
            Type: KgNodeTypes.Year,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/22_BBY",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Year),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = KgNodeTypes.Character }
        );
    }

    [TestMethod]
    [DataRow("Chancellor", "has_chancellor")]
    [DataRow("Head", "has_head")]
    [DataRow("Chief", "has_chief")]
    public void TypedLeaderField_EmitsTypedEdge(string fieldLabel, string expectedLabel)
    {
        var ctx = BuildYearCtx(fieldLabel);
        var result = new DefaultNodeBuilder(KgNodeTypes.Year).Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual(expectedLabel, result.Edges[0].Label);
    }
}
