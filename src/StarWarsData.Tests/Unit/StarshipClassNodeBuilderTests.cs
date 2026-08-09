using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C5 (StarshipClass half): Affiliation → Religion / Species
/// relabels to <c>designed_for</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class StarshipClassNodeBuilderTests
{
    static (NodeBuilderContext ctx, StarshipClassNodeBuilder builder) BuildAffiliationCase(string targetType) =>
        (NodeBuilderContexts.SingleLinkedField(KgNodeTypes.StarshipClass, "Affiliation", "Sith Order", targetType, sourceTitle: "Sith infiltrator"), new StarshipClassNodeBuilder());

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
