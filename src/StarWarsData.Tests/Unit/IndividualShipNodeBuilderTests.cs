using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C5 (IndividualShip half): Affiliation → Military_unit /
/// Fleet relabels to <c>assigned_to</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class IndividualShipNodeBuilderTests
{
    static (NodeBuilderContext ctx, IndividualShipNodeBuilder builder) BuildAffiliationCase(string targetType) =>
        (NodeBuilderContexts.SingleLinkedField(KgNodeTypes.IndividualShip, "Affiliation", "501st Legion", targetType, sourceTitle: "Devastator"), new IndividualShipNodeBuilder());

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
