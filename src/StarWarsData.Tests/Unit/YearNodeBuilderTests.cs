using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Tests.Infrastructure;

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
    static NodeBuilderContext BuildYearCtx(string fieldLabel, string targetTitle = "Palpatine") =>
        NodeBuilderContexts.SingleLinkedField(KgNodeTypes.Year, fieldLabel, targetTitle, KgNodeTypes.Character, sourceTitle: "22 BBY");

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
