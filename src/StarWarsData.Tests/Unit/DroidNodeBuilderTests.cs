using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B (Pattern A) for <see cref="DroidNodeBuilder"/>.
/// Droid Affiliation → Character/Person semantically means ownership.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class DroidNodeBuilderTests
{
    static (NodeBuilderContext ctx, DroidNodeBuilder builder) BuildAffiliationCase(string targetType)
    {
        const int targetPageId = 200;
        const string targetTitle = "Owner";
        const string targetUrl = "/wiki/Owner";

        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Affiliation" },
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

        var ctx = new NodeBuilderContext(
            PageId: 100,
            Title: "R2-D2",
            Type: KgNodeTypes.Droid,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/R2-D2",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Droid),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );

        return (ctx, new DroidNodeBuilder());
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Character)]
    [DataRow(KgNodeTypes.Person)]
    public void Affiliation_ToCharacterOrPerson_RelabelsToOwnedBy(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("owned_by", result.Edges[0].Label);
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Government)]
    [DataRow(KgNodeTypes.Organization)]
    [DataRow("Military_unit")]
    public void Affiliation_NonOwnerTarget_KeepsAffiliatedWith(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("affiliated_with", result.Edges[0].Label);
    }
}
