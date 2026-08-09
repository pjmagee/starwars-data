using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase B (Pattern A) for <see cref="DroidNodeBuilder"/>.
/// Droid Affiliation → Character/Person semantically means ownership.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class DroidNodeBuilderTests
{
    static (NodeBuilderContext ctx, DroidNodeBuilder builder) BuildAffiliationCase(string targetType) =>
        (NodeBuilderContexts.SingleLinkedField(KgNodeTypes.Droid, "Affiliation", "Owner", targetType, sourceTitle: "R2-D2"), new DroidNodeBuilder());

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
