using StarWarsData.Models.Entities;
using MongoDB.Bson;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B (Pattern A — source × target relabel) for
/// <see cref="CharacterNodeBuilder"/>. Each test simulates a Character page
/// whose <c>Affiliation</c> field links to a single target of a specific type
/// and asserts the emitted edge's canonical label is the per-type relabel
/// (clean replacement: <c>affiliated_with</c> is gone for that triple).
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class CharacterNodeBuilderTests
{
    static (NodeBuilderContext ctx, CharacterNodeBuilder builder) BuildAffiliationCase(string targetType, string targetTitle = "Target", int targetPageId = 200) =>
        (NodeBuilderContexts.SingleLinkedField(KgNodeTypes.Character, "Affiliation(s)", targetTitle, targetType, sourceTitle: "Anakin", targetPageId: targetPageId), new CharacterNodeBuilder());

    [TestMethod]
    [DataRow(KgNodeTypes.TitleOrPosition, "has_role")]
    [DataRow(KgNodeTypes.Family, "member_of_family")]
    [DataRow("Religion", "member_of")]
    [DataRow(KgNodeTypes.Species, "has_ethnicity")]
    [DataRow(KgNodeTypes.City, "from_city")]
    [DataRow("Company", "works_for")]
    [DataRow("Military_unit", "serves_in")]
    [DataRow("Fleet", "serves_in")]
    public void Affiliation_TargetType_RelabelsToPerTypeLabel(string targetType, string expectedLabel)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);

        Assert.AreEqual(1, result.Edges.Count, "Expected one edge from Affiliation field.");
        var edge = result.Edges[0];
        Assert.AreEqual(expectedLabel, edge.Label, $"Affiliation → {targetType} should relabel to {expectedLabel}.");
        // SourceFieldLabel preserved for audit
        Assert.AreEqual("Affiliation(s)", edge.Meta?.SourceFieldLabel);
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Government)]
    [DataRow(KgNodeTypes.Organization)]
    [DataRow(KgNodeTypes.Structure)]
    [DataRow(KgNodeTypes.Character)]
    public void Affiliation_NonTrackedTargetType_KeepsAffiliatedWith(string targetType)
    {
        var (ctx, builder) = BuildAffiliationCase(targetType);
        var result = builder.Build(ctx);

        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("affiliated_with", result.Edges[0].Label, $"Target type {targetType} keeps the generic label.");
    }

    [TestMethod]
    public void NonAffiliationField_NotRelabeled()
    {
        // A Masters field (apprentice_of) should NOT be touched even when target is TitleOrPosition.
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Masters" },
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

        var ctx = NodeBuilderContexts.FromDataItems(
            KgNodeTypes.Character,
            dataItems,
            sourceTitle: "Anakin",
            wikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["/wiki/Yoda"] = 200, ["Yoda"] = 200 },
            nodeTypeByPageId: new Dictionary<int, string> { [200] = KgNodeTypes.TitleOrPosition }
        );

        var result = new CharacterNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("apprentice_of", result.Edges[0].Label, "Masters field should not be relabeled.");
    }
}
