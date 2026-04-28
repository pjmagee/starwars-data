using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C5 (StarshipClass half): Affiliation → Religion / Species
/// relabels to <c>designed_for</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class StarshipClassNodeBuilderTests
{
    static (NodeBuilderContext ctx, StarshipClassNodeBuilder builder) BuildAffiliationCase(string targetType)
    {
        const int targetPageId = 200;
        const string targetTitle = "Sith Order";
        const string targetUrl = "/wiki/Sith_Order";

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
            Title: "Sith infiltrator",
            Type: KgNodeTypes.StarshipClass,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Sith_infiltrator",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.StarshipClass),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );

        return (ctx, new StarshipClassNodeBuilder());
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Religion)]
    [DataRow(KgNodeTypes.Species)]
    public void Affiliation_ToReligionOrSpecies_RelabelsToDesignedFor(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("designed_for", result.Edges[0].Label);
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Government)]
    [DataRow(KgNodeTypes.Organization)]
    [DataRow("Military_unit")]
    public void Affiliation_OtherTargets_KeepsAffiliatedWith(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("affiliated_with", result.Edges[0].Label);
    }
}
