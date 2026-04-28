using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B for <see cref="TitleOrPositionNodeBuilder"/>.
/// Verifies <c>Organization</c> and <c>Government</c> fields on a TitleOrPosition
/// page relabel to <c>position_in</c> via the override (Pattern A — source ×
/// target relabel needed because the global FieldSemantics has different
/// semantics for these field names).
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class TitleOrPositionNodeBuilderTests
{
    static NodeBuilderContext BuildCtx(string fieldLabel, string targetType)
    {
        const int targetPageId = 200;
        const string targetTitle = "Galactic Senate";
        const string targetUrl = "/wiki/Galactic_Senate";
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
            Title: "Senator",
            Type: KgNodeTypes.TitleOrPosition,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Senator",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.TitleOrPosition),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );
    }

    [TestMethod]
    [DataRow("Organization", KgNodeTypes.Organization)]
    [DataRow("Organization", KgNodeTypes.Government)]
    [DataRow("Government", KgNodeTypes.Organization)]
    [DataRow("Government", KgNodeTypes.Government)]
    public void OrgOrGovernmentField_RelabelsToPositionIn(string fieldLabel, string targetType)
    {
        var ctx = BuildCtx(fieldLabel, targetType);
        var result = new TitleOrPositionNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("position_in", result.Edges[0].Label);
    }
}
