using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

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
    static NodeBuilderContext BuildCtx(string fieldLabel, string targetType) =>
        NodeBuilderContexts.SingleLinkedField(KgNodeTypes.TitleOrPosition, fieldLabel, "Galactic Senate", targetType, sourceTitle: "Senator");

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
