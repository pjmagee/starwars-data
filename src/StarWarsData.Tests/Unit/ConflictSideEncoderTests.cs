using StarWarsData.Models.Entities;
using MongoDB.Bson;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B (Pattern B — per-side encoding) for the conflict-type
/// builders. Verifies <see cref="EdgeMeta.SideIndex"/> is stamped from the
/// trailing digit of <c>SourceFieldLabel</c>, and that commander edges with
/// implausible target types are dropped.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class ConflictSideEncoderTests
{
    static NodeBuilderContext BuildBattleContext(BsonArray dataItems, IDictionary<int, string> nodeTypeByPageId, IDictionary<string, int> wikiUrlToPageId) =>
        NodeBuilderContexts.FromDataItems(
            KgNodeTypes.Battle,
            dataItems,
            sourceTitle: "Battle of Geonosis",
            sourcePageId: 1,
            wikiUrlToPageId: new Dictionary<string, int>(wikiUrlToPageId, StringComparer.OrdinalIgnoreCase),
            nodeTypeByPageId: new Dictionary<int, string>(nodeTypeByPageId)
        );

    [TestMethod]
    [DataRow("commanders1", 1)]
    [DataRow("commanders2", 2)]
    [DataRow("commanders3", 3)]
    [DataRow("commanders4", 4)]
    [DataRow("ppl1", 1)]
    [DataRow("unit2", 2)]
    [DataRow("side3", 3)]
    public void NumberedSideField_StampsSideIndex(string fieldLabel, int expectedSide)
    {
        const int targetPageId = 200;
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, fieldLabel },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { "Yoda" }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, "Yoda" }, { InfoboxBsonFields.Href, "/wiki/Yoda" } },
                    }
                },
            },
        };

        var ctx = BuildBattleContext(
            dataItems,
            new Dictionary<int, string> { [targetPageId] = KgNodeTypes.Character },
            new Dictionary<string, int> { ["/wiki/Yoda"] = targetPageId, ["Yoda"] = targetPageId }
        );

        var result = new BattleNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        var edge = result.Edges[0];
        Assert.IsNotNull(edge.Meta);
        Assert.AreEqual(expectedSide, edge.Meta!.SideIndex);
        Assert.AreEqual(fieldLabel, edge.Meta.SourceFieldLabel);
    }

    [TestMethod]
    [DataRow("Religion")]
    [DataRow(KgNodeTypes.CelestialBody)]
    [DataRow(KgNodeTypes.Species)]
    [DataRow(KgNodeTypes.Family)]
    [DataRow("Company")]
    public void CommanderEdge_WithImplausibleTargetType_IsDropped(string badTargetType)
    {
        const int targetPageId = 200;
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "commanders1" },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { "X" }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, "X" }, { InfoboxBsonFields.Href, "/wiki/X" } },
                    }
                },
            },
        };

        var ctx = BuildBattleContext(dataItems, new Dictionary<int, string> { [targetPageId] = badTargetType }, new Dictionary<string, int> { ["/wiki/X"] = targetPageId, ["X"] = targetPageId });

        var result = new BattleNodeBuilder().Build(ctx);
        Assert.AreEqual(0, result.Edges.Count, $"Commander → {badTargetType} should be dropped as implausible.");
    }

    [TestMethod]
    public void NonNumberedField_DoesNotStampSideIndex()
    {
        // Battles also have "Place" / "Conflict" fields. They're not per-side.
        const int targetPageId = 300;
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Place" },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { "Geonosis" }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, "Geonosis" }, { InfoboxBsonFields.Href, "/wiki/Geonosis" } },
                    }
                },
            },
        };

        var ctx = BuildBattleContext(
            dataItems,
            new Dictionary<int, string> { [targetPageId] = KgNodeTypes.CelestialBody },
            new Dictionary<string, int> { ["/wiki/Geonosis"] = targetPageId, ["Geonosis"] = targetPageId }
        );

        var result = new BattleNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.IsNull(result.Edges[0].Meta?.SideIndex, "Place field is not per-side.");
    }
}
