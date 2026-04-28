using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C5 (IndividualShip half): Affiliation → Military_unit /
/// Fleet relabels to <c>assigned_to</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class IndividualShipNodeBuilderTests
{
    static (NodeBuilderContext ctx, IndividualShipNodeBuilder builder) BuildAffiliationCase(string targetType)
    {
        const int targetPageId = 200;
        const string targetTitle = "501st Legion";
        const string targetUrl = "/wiki/501st_Legion";

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
            Title: "Devastator",
            Type: KgNodeTypes.IndividualShip,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Devastator",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.IndividualShip),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );

        return (ctx, new IndividualShipNodeBuilder());
    }

    [TestMethod]
    [DataRow("Military_unit")]
    [DataRow("Fleet")]
    public void Affiliation_ToMilitaryOrFleet_RelabelsToAssignedTo(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("assigned_to", result.Edges[0].Label);
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Government)]
    [DataRow(KgNodeTypes.Organization)]
    [DataRow(KgNodeTypes.Religion)]
    public void Affiliation_OtherTargets_KeepsAffiliatedWith(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("affiliated_with", result.Edges[0].Label);
    }
}
