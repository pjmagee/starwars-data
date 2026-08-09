using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C1: source-aware language relabel for
/// <see cref="CelestialBodyNodeBuilder"/>. Planets host languages, they don't
/// speak them — relabel <c>speaks_language</c> → <c>has_language</c> when the
/// source is a CelestialBody.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class CelestialBodyNodeBuilderTests
{
    // CelestialBody's actual field name is "Primary language(s)" — see TemplateFields.g.cs.
    // FieldSemantics maps it (and "Language", "Language(s)") all to speaks_language.
    static (NodeBuilderContext ctx, CelestialBodyNodeBuilder builder) BuildLanguageCase(string targetType) =>
        (NodeBuilderContexts.SingleLinkedField(KgNodeTypes.CelestialBody, "Primary language(s)", "Basic", targetType, sourceTitle: "Tatooine"), new CelestialBodyNodeBuilder());

    [TestMethod]
    public void Language_ToLanguageTarget_RelabelsToHasLanguage()
    {
        var (ctx, builder) = BuildLanguageCase(KgNodeTypes.Language);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_language", result.Edges[0].Label);
        Assert.AreEqual("Primary language(s)", result.Edges[0].Meta?.SourceFieldLabel);
    }

    [TestMethod]
    [DataRow(KgNodeTypes.Species)]
    [DataRow("")]
    [DataRow("CulturalGroup")]
    public void Language_NonLanguageTarget_KeepsSpeaksLanguage(string targetType)
    {
        // If the target isn't Language-typed (e.g. unresolved link or a
        // wrong-typed link to a Species page) keep the canonical FieldSemantics label.
        var (ctx, builder) = BuildLanguageCase(targetType);
        var result = builder.Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("speaks_language", result.Edges[0].Label);
    }
}
