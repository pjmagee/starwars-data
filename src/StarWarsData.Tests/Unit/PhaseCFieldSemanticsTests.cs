using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — covers field-promotion changes that live entirely in
/// <see cref="FieldSemantics"/> (no per-type override needed). C4 (ReferenceMagazine
/// Featured), C8 (Food Race / Inedible by), C9 (Character Domain / Caste).
/// These run through <see cref="UnknownNodeBuilder"/> for the types we don't
/// have explicit per-type builders for; the generic loop classifies them via
/// the global FieldSemantics dictionary.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class PhaseCFieldSemanticsTests
{
    static NodeBuilderContext BuildCtx(string sourceType, string fieldLabel, string targetTitle, string targetType)
    {
        const int targetPageId = 200;
        var targetUrl = $"/wiki/{targetTitle.Replace(' ', '_')}";

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
            Title: "Source Page",
            Type: sourceType,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Source_Page",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(sourceType),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );
    }

    // ── C4: ReferenceMagazine "Featured" → features ──
    [TestMethod]
    public void ReferenceMagazine_Featured_PromotesToFeatures()
    {
        var ctx = BuildCtx(KgNodeTypes.ReferenceMagazine, "Featured", "Boba Fett", KgNodeTypes.Character);
        // ReferenceMagazine has no dedicated builder; UnknownNodeBuilder runs the generic loop.
        var result = new UnknownNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("features", result.Edges[0].Label);
    }

    // ── C8: Food fields ──
    [TestMethod]
    public void Food_Race_AliasesToEdibleBy()
    {
        // "Race" on Food semantically duplicates "Edible by" — both target Species.
        var ctx = BuildCtx(KgNodeTypes.Food, "Race", "Wookiee", KgNodeTypes.Species);
        var result = new UnknownNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("edible_by", result.Edges[0].Label);
    }

    [TestMethod]
    public void Food_InedibleBy_PromotesToNotEdibleBy()
    {
        var ctx = BuildCtx(KgNodeTypes.Food, "Inedible by", "Hutt", KgNodeTypes.Species);
        var result = new UnknownNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("not_edible_by", result.Edges[0].Label);
    }

    // ── C9: Character Yuuzhan Vong concepts ──
    [TestMethod]
    public void Character_Domain_PromotesToHasDomain()
    {
        var ctx = BuildCtx(KgNodeTypes.Character, "Domain", "Domain Shai", KgNodeTypes.Religion);
        var result = new CharacterNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_domain", result.Edges[0].Label);
    }

    [TestMethod]
    public void Character_Caste_PromotesToHasCaste()
    {
        var ctx = BuildCtx(KgNodeTypes.Character, "Caste", "Warrior Caste", KgNodeTypes.Religion);
        var result = new CharacterNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("has_caste", result.Edges[0].Label);
    }

    // ── C7: associated_culture target widening (informational only) ──
    [TestMethod]
    public void AssociatedCulture_TargetTypesIncludeSpeciesAndReligion()
    {
        // Verify the dictionary entry is widened to reflect the actual data shape.
        var def = FieldSemantics.Relationships["Culture"];
        CollectionAssert.Contains(def.ExpectedTargetTypes, KgNodeTypes.Species);
        CollectionAssert.Contains(def.ExpectedTargetTypes, KgNodeTypes.Religion);
    }
}
