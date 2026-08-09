using System.Reflection;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Asserts the <see cref="KgNodeTypes"/> constants stay in lockstep with the
/// real registration list from <see cref="NodeBuilderRegistry"/>. Catches three
/// classes of drift:
///
/// <list type="number">
///   <item><b>Constant without builder</b> — adding <c>KgNodeTypes.Foo</c> without
///   registering a builder silently routes <c>Foo</c>-typed nodes to the Unknown
///   <see cref="DefaultNodeBuilder"/>. Test fails on the missing registration.</item>
///   <item><b>Registered builder without constant</b> — a <c>NodeType</c> string
///   that isn't backed by a <see cref="KgNodeTypes"/> constant means the builder
///   works but is referred to by raw string everywhere (the case of the dead
///   <c>MilitaryNodeBuilder</c> that returned <c>"Military"</c> instead of
///   <c>"Military_unit"</c>).</item>
///   <item><b>Mismatched constant value</b> — every registered <c>NodeType</c>
///   must resolve to a <c>KgNodeTypes</c> constant value.</item>
/// </list>
///
/// Registration is the single source of truth (not reflection over concrete
/// classes) because <see cref="DefaultNodeBuilder"/> is parameterized and has
/// no parameterless constructor.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class NodeBuilderCoverageTests
{
    /// <summary>
    /// Constants intentionally without a registered builder. Every public string
    /// constant in <see cref="KgNodeTypes"/> currently has a registered builder
    /// except the set below. Add to this set if a constant is purely a
    /// reference-only type (e.g. used as an edge-target classifier but never
    /// expected to be a primary node template).
    /// </summary>
    static readonly HashSet<string> ConstantsWithoutBuilders = new(StringComparer.OrdinalIgnoreCase)
    {
        // KgNodeTypes.Language is referenced as a target type by edges from
        // CelestialBody / Species / System but rarely appears as a primary
        // node template in raw.pages. Generic loop handles it via the Unknown
        // DefaultNodeBuilder if a Language node ever lands as a template.
        KgNodeTypes.Language,
        // KgNodeTypes.Religion + KgNodeTypes.Deity — same shape, primarily
        // used as edge-target classifiers.
        KgNodeTypes.Religion,
        KgNodeTypes.Deity,
        // KgNodeTypes.Tactics, Disease, Medal — small-population types
        // (~110-180 nodes each) that share the generic infobox shape; no
        // per-type semantics warrant a builder. Add explicit builder if a
        // need surfaces.
        KgNodeTypes.Tactics,
        KgNodeTypes.Disease,
        KgNodeTypes.Medal,
        // Lower-population publication subtypes — they share the generic
        // infobox shape with their primary types (Book, Comic, etc.) and
        // don't have unique extraction semantics.
        KgNodeTypes.BookSeries,
        KgNodeTypes.ComicSeries,
        KgNodeTypes.ComicArc,
        KgNodeTypes.MagazineDepartment,
        KgNodeTypes.VideoGame,
        KgNodeTypes.ActivityBook,
        KgNodeTypes.TradingCardSet,
        KgNodeTypes.Music,
        KgNodeTypes.Soundtrack,
        KgNodeTypes.Band,
        KgNodeTypes.WebArticle,
        KgNodeTypes.Store,
        KgNodeTypes.RealCompany,
        KgNodeTypes.AquaticVehicle,
    };

    [TestMethod]
    public void Every_KgNodeTypes_constant_has_a_registered_NodeBuilder_or_is_explicitly_excluded()
    {
        var allConstants = GetKgNodeTypesConstants();
        var registeredNodeTypes = GetAllRegisteredNodeBuilderTypes();

        var missing = allConstants.Where(c => !ConstantsWithoutBuilders.Contains(c) && !registeredNodeTypes.Contains(c)).OrderBy(c => c).ToList();

        Assert.IsTrue(
            missing.Count == 0,
            $"The following KgNodeTypes constants have no registered NodeBuilder and are not in ConstantsWithoutBuilders: {string.Join(", ", missing)}.\n"
                + "Either: (a) register a DefaultNodeBuilder (or real-logic builder) in NodeBuilderRegistry.CreateBuilders, OR (b) add the constant to ConstantsWithoutBuilders with a justifying comment."
        );
    }

    [TestMethod]
    public void Every_registered_builder_NodeType_is_a_KgNodeTypes_constant_value()
    {
        var allConstantValues = new HashSet<string>(GetKgNodeTypesConstants(), StringComparer.OrdinalIgnoreCase);
        var builders = NodeBuilderRegistry.CreateBuilders();

        var unbacked = new List<string>();
        foreach (var (key, builder) in builders)
        {
            // Unknown is intentionally registered as the catch-all and IS a KgNodeTypes constant.
            if (!allConstantValues.Contains(builder.NodeType))
                unbacked.Add($"{builder.GetType().Name}(key={key}) → NodeType=\"{builder.NodeType}\"");
        }

        Assert.IsTrue(
            unbacked.Count == 0,
            $"The following registered builders have a NodeType not backed by a KgNodeTypes constant: {string.Join("; ", unbacked)}.\n"
                + "Add a constant to KgNodeTypes and reference it from the registration (avoid raw string literals)."
        );
    }

    [TestMethod]
    public void Registry_keys_match_each_builder_NodeType()
    {
        var builders = NodeBuilderRegistry.CreateBuilders();
        var mismatches = builders
            .Where(kv => !string.Equals(kv.Key, kv.Value.NodeType, StringComparison.Ordinal))
            .Select(kv => $"key=\"{kv.Key}\" vs NodeType=\"{kv.Value.NodeType}\"")
            .ToList();

        Assert.IsTrue(mismatches.Count == 0, $"Registry key/NodeType mismatches: {string.Join("; ", mismatches)}");
    }

    static IEnumerable<string> GetKgNodeTypesConstants()
    {
        var fields = typeof(KgNodeTypes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
        return fields.Select(f => (string)f.GetRawConstantValue()!).Where(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>
    /// Consults the real registration list — the same factory
    /// <see cref="T:StarWarsData.Services.InfoboxGraphService"/> uses at runtime.
    /// </summary>
    static HashSet<string> GetAllRegisteredNodeBuilderTypes() => new(NodeBuilderRegistry.CreateBuilders().Keys, StringComparer.OrdinalIgnoreCase);
}
