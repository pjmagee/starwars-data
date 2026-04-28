using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

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
    static (NodeBuilderContext ctx, CelestialBodyNodeBuilder builder) BuildLanguageCase(string targetType)
    {
        const int targetPageId = 200;
        const string targetTitle = "Basic";
        const string targetUrl = "/wiki/Galactic_Basic_Standard";

        // CelestialBody's actual field name is "Primary language(s)" — see TemplateFields.g.cs.
        // FieldSemantics maps it (and "Language", "Language(s)") all to speaks_language.
        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, "Primary language(s)" },
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
            Title: "Tatooine",
            Type: KgNodeTypes.CelestialBody,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Tatooine",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.CelestialBody),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );

        return (ctx, new CelestialBodyNodeBuilder());
    }

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
