using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C6: drop <c>led_by</c> edges where the target is another
/// Organization (parser noise — orgs aren't led by orgs, people are).
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class OrganizationNodeBuilderTests
{
    static (NodeBuilderContext ctx, OrganizationNodeBuilder builder) BuildLeaderCase(string targetType, string targetTitle = "Some Leader")
    {
        const int targetPageId = 200;
        var targetUrl = $"/wiki/{targetTitle.Replace(' ', '_')}";

        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Leader(s)" },
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
            Title: "Test Org",
            Type: KgNodeTypes.Organization,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Test_Org",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Organization),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );

        return (ctx, new OrganizationNodeBuilder());
    }

    [TestMethod]
    public void LedBy_TargetIsOrganization_EdgeDropped()
    {
        var (ctx, builder) = BuildLeaderCase(KgNodeTypes.Organization);
        var result = builder.Build(ctx);
        Assert.AreEqual(0, result.Edges.Count, "led_by → Organization edges must be dropped as parser noise.");
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Character)]
    [DataRow(KgNodeTypes.Person)]
    public void LedBy_TargetIsPerson_EdgePreserved(string targetType)
    {
        var (ctx, builder) = BuildLeaderCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("led_by", result.Edges[0].Label);
    }
}
