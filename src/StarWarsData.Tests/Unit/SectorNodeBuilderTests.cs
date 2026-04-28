using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B (Pattern C — field-alias collapse) for
/// <see cref="SectorNodeBuilder"/>. Many distinct war-named and era-named
/// Sector fields collapse to <c>has_conflict</c> / <c>in_era</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class SectorNodeBuilderTests
{
    static NodeBuilderContext BuildSectorCtx(string fieldLabel, string targetType = "Battle")
    {
        const int targetPageId = 200;
        const string targetTitle = "Some War";
        const string targetUrl = "/wiki/Some_War";
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
            Title: "Test Sector",
            Type: KgNodeTypes.Sector,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Test_Sector",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Sector),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );
    }

    [TestMethod]
    [DataRow("Yuuzhan Vong War")]
    [DataRow("Mandalorian Wars")]
    [DataRow("Great Sith War")]
    [DataRow("First Order-Resistance War")]
    [DataRow("Imperial Period conflicts")]
    [DataRow("Yinchorri Uprising")]
    [DataRow("Reslian Purge")]
    [DataRow("Berch Teller campaign")]
    [DataRow("Belderone Contention")]
    [DataRow("Chiss Ascendancy crisis")]
    [DataRow("Lost Tribe of Sith emergence")]
    [DataRow("Reconquest of the Rim")]
    [DataRow("Kanz Disorders")]
    public void WarNamedField_RelabelsToHasConflict(string fieldLabel)
    {
        var ctx = BuildSectorCtx(fieldLabel);
        var result = new SectorNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_conflict", result.Edges[0].Label, $"Field '{fieldLabel}' should collapse to has_conflict.");
    }

    [TestMethod]
    [DataRow("Rise of the Empire era")]
    [DataRow("Imperial Era")]
    [DataRow("New Republic era")]
    [DataRow("New Jedi Order era")]
    [DataRow("Old Republic era")]
    [DataRow("Legacy era")]
    [DataRow("Imperial Period")]
    public void EraNamedField_RelabelsToHasConflict(string fieldLabel)
    {
        // Sample inspection shows era-named fields link to Battle / War / Mission
        // targets (events that happened in that era), not to Era nodes — so they
        // collapse to has_conflict alongside the named conflict fields.
        var ctx = BuildSectorCtx(fieldLabel, targetType: KgNodeTypes.Battle);
        var result = new SectorNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_conflict", result.Edges[0].Label, $"Era-named field '{fieldLabel}' should collapse to has_conflict.");
    }

    [TestMethod]
    public void GalacticCivilWar_AlreadyCanonical_PreservesHasConflict()
    {
        // "Galactic Civil War" is already in FieldSemantics → has_conflict.
        // The override must not double-relabel or break it.
        var ctx = BuildSectorCtx("Galactic Civil War");
        var result = new SectorNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_conflict", result.Edges[0].Label);
    }

    [TestMethod]
    public void StructuralFields_NotAffected()
    {
        // "Region(s)" is a structural field on Sector — not war or era. Should NOT relabel.
        var ctx = BuildSectorCtx("Region(s)", targetType: KgNodeTypes.Region);
        var result = new SectorNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("in_region", result.Edges[0].Label, "Region(s) should keep its FieldSemantics label.");
    }
}
