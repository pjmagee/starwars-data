using StarWarsData.Services.AI.Agents.Holocron.Tools;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Phase A.1 unit coverage for the non-Mongo surface of <see cref="HolocronReadToolkit"/>:
/// <c>find_canonical_label</c> (filters <see cref="StarWarsData.Services.KnowledgeGraph.Definitions.FieldSemantics"/>
/// by target type) and <c>get_template_schema</c> (template-scoped slice via
/// <see cref="StarWarsData.Services.KnowledgeGraph.Definitions.InfoboxDefinitionRegistry"/>).
/// The Mongo-backed methods (<c>resolve_entity</c>, <c>check_existing_edges</c>) are
/// covered by the integration tier.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class HolocronToolsTests
{
    static HolocronReadToolkit BuildToolkit() => new(new MongoDB.Driver.MongoClient("mongodb://localhost:27017"), "test-noop");

    // ── FindCanonicalLabel ─────────────────────────────────────────────────────

    [TestMethod]
    public void FindCanonicalLabel_TitleOrPositionTarget_IncludesHasRole()
    {
        var options = BuildToolkit().FindCanonicalLabel("Character", "TitleOrPosition");

        // has_role is the Design-024 canonical for Character→TitleOrPosition. The
        // synthetic FieldSemantics seed `__has_role` declares TitleOrPosition as
        // the only target — so target-side filtering surfaces it without a
        // synonym layer.
        Assert.IsTrue(options.Any(o => o.Label == "has_role"), "Expected 'has_role' in options for Character→TitleOrPosition.");

        // affiliated_with declares targets [Organization, Government] — must NOT
        // appear for a TitleOrPosition target. This is the "wrong-typed edge"
        // failure mode (Design-025 §Failure-mode mapping row 2) prevented at
        // discovery time.
        Assert.IsFalse(options.Any(o => o.Label == "affiliated_with"), "'affiliated_with' must not surface for TitleOrPosition target.");
    }

    [TestMethod]
    public void FindCanonicalLabel_BattleTarget_IncludesParticipatedIn()
    {
        var options = BuildToolkit().FindCanonicalLabel("Character", "Battle");
        Assert.IsTrue(options.Any(o => o.Label == "present_for_battle" || o.Label == "has_conflict"), "Expected a Battle-targeting label in options.");
    }

    [TestMethod]
    public void FindCanonicalLabel_PermissiveLabels_AlwaysIncluded()
    {
        // followed_by / preceded_by / part_of declare empty ExpectedTargetTypes —
        // they're sequence labels valid for any target. Must surface regardless
        // of targetType.
        var bookOptions = BuildToolkit().FindCanonicalLabel("Book", "Book");
        Assert.IsTrue(bookOptions.Any(o => o.Label == "followed_by"));

        var characterOptions = BuildToolkit().FindCanonicalLabel("Character", "Character");
        Assert.IsTrue(characterOptions.Any(o => o.Label == "followed_by"));
    }

    [TestMethod]
    public void FindCanonicalLabel_UnknownTargetType_ReturnsOnlyPermissiveLabels()
    {
        // A made-up target type matches no labels' ExpectedTargetTypes. Only the
        // permissive (empty-targets) labels survive.
        var options = BuildToolkit().FindCanonicalLabel("Character", "ZZZNotARealType");
        Assert.IsTrue(options.Count > 0, "Permissive labels should still surface.");
        Assert.IsTrue(options.All(o => o.ExpectedTargetTypes.Count == 0), "All surviving labels should be permissive (no declared target types).");
    }

    [TestMethod]
    public void FindCanonicalLabel_EmptyTargetType_ReturnsEmpty()
    {
        // Without a target type the tool can't filter — return empty rather than
        // dump the entire vocabulary on the agent.
        Assert.AreEqual(0, BuildToolkit().FindCanonicalLabel("Character", string.Empty).Count);
        Assert.AreEqual(0, BuildToolkit().FindCanonicalLabel("Character", "  ").Count);
    }

    [TestMethod]
    public void FindCanonicalLabel_DistinctByLabel_NoDuplicateRows()
    {
        // FieldSemantics has many fields that map to the same canonical label
        // (e.g. Region / Region(s) / Regions all → in_region). The list must
        // expose each canonical label once, not once per field.
        var options = BuildToolkit().FindCanonicalLabel("Character", "Character");
        var distinctCount = options.Select(o => o.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.AreEqual(options.Count, distinctCount, "Each canonical label should appear exactly once.");
    }

    // ── GetTemplateSchema ──────────────────────────────────────────────────────

    [TestMethod]
    public void GetTemplateSchema_KnownTemplate_ReturnsClassifiedFields()
    {
        var schema = BuildToolkit().GetTemplateSchema("Character");

        Assert.AreEqual("Character", schema.Template, ignoreCase: true);
        Assert.IsTrue(schema.Properties.Count > 0, "Character template should have at least one property.");
        Assert.IsTrue(schema.Relationships.Count > 0, "Character template should have at least one relationship.");

        // Spot-check: Masters is a known Character relationship.
        var masters = schema.Relationships.FirstOrDefault(r => r.Field.Equals("Masters", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(masters);
        Assert.AreEqual("apprentice_of", masters.Label);
    }

    [TestMethod]
    public void GetTemplateSchema_UnknownTemplate_ReturnsEmptyDefinition()
    {
        var schema = BuildToolkit().GetTemplateSchema("NonExistentTemplateZZZ");

        // Unknown templates fall through to InfoboxDefinitionRegistry's empty
        // per-template definition (no fields). The Fallback (full semantics)
        // is reserved for null/empty input — see InfoboxDefinitionRegistry.ForTemplate.
        Assert.AreEqual("NonExistentTemplateZZZ", schema.Template, ignoreCase: true);
        Assert.AreEqual(0, schema.Properties.Count);
        Assert.AreEqual(0, schema.Relationships.Count);
    }

    [TestMethod]
    public void GetTemplateSchema_TemporalFields_AreSurfaced()
    {
        var schema = BuildToolkit().GetTemplateSchema("Character");

        // "Born" and "Died" are temporal fields on Character.
        var born = schema.TemporalFields.FirstOrDefault(t => t.Field.Equals("Born", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(born);
        Assert.AreEqual("lifespan.start", born.Semantic);
    }

    [TestMethod]
    public void Toolkit_AsAIFunctions_ProducesFourTools()
    {
        var tools = BuildToolkit().AsAIFunctions();

        Assert.AreEqual(4, tools.Count);
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.IsTrue(names.Contains("resolve_entity"));
        Assert.IsTrue(names.Contains("find_canonical_label"));
        Assert.IsTrue(names.Contains("check_existing_edges"));
        Assert.IsTrue(names.Contains("get_template_schema"));
    }
}
