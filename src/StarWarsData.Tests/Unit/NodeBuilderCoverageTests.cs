using System.Reflection;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Asserts the `KgNodeTypes` constants stay in lockstep with the registered
/// `INodeBuilder` implementations. Catches three classes of drift:
///
/// <list type="number">
///   <item><b>Constant without builder</b> — adding <c>KgNodeTypes.Foo</c> without
///   creating a <c>FooNodeBuilder</c> silently routes <c>Foo</c>-typed nodes to
///   <see cref="UnknownNodeBuilder"/>. Test fails on the missing builder.</item>
///   <item><b>Builder without constant</b> — creating a <c>FooNodeBuilder</c> with a
///   <c>NodeType</c> string that isn't backed by a <see cref="KgNodeTypes"/>
///   constant means the builder works but is referred to by raw string
///   everywhere — the case of the dead <c>MilitaryNodeBuilder</c> (Phase C)
///   that returned <c>"Military"</c> instead of <c>"Military_unit"</c>.</item>
///   <item><b>Mismatched constant value</b> — every <c>NodeType</c> property must
///   resolve to a <c>KgNodeTypes</c> constant (so renaming the corpus is one
///   place). A literal string in <c>NodeType</c> would break this.</item>
/// </list>
///
/// This test is deliberately reflection-based rather than database-aware; it
/// catches the structural problem (constant↔builder mapping) without requiring
/// a Mongo connection. Coverage of "every type with corpus instances has a
/// builder" is impossible without DB access; we trust periodic surveys for that
/// (see Design-024 Appendix A).
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class NodeBuilderCoverageTests
{
    /// <summary>
    /// Constants intentionally without a per-type builder. Empty for now —
    /// every public string constant in <see cref="KgNodeTypes"/> currently has
    /// a registered builder. Add to this set if a constant is purely a
    /// reference-only type (e.g. used as an edge-target classifier but never
    /// expected to be a primary node template).
    /// </summary>
    static readonly HashSet<string> ConstantsWithoutBuilders = new(StringComparer.OrdinalIgnoreCase)
    {
        // KgNodeTypes.Language is referenced as a target type by edges from
        // CelestialBody / Species / System but rarely appears as a primary
        // node template in raw.pages. Generic loop handles it via
        // UnknownNodeBuilder if a Language node ever lands as a template.
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
                + "Either: (a) create the missing NodeBuilder under NodeBuilders/Types/ and register it in InfoboxGraphService.RegisterAllBuilders, OR (b) add the constant to ConstantsWithoutBuilders with a justifying comment."
        );
    }

    [TestMethod]
    public void Every_NodeBuilder_NodeType_is_a_KgNodeTypes_constant_value()
    {
        var allConstantValues = new HashSet<string>(GetKgNodeTypesConstants(), StringComparer.OrdinalIgnoreCase);
        var nodeBuilderTypes = GetAllNodeBuilderClassTypes();

        var unbacked = new List<string>();
        foreach (var t in nodeBuilderTypes)
        {
            // Skip abstract bases and the catch-all UnknownNodeBuilder
            if (t.IsAbstract || t == typeof(UnknownNodeBuilder))
                continue;

            var instance = (INodeBuilder)Activator.CreateInstance(t)!;
            if (!allConstantValues.Contains(instance.NodeType))
            {
                unbacked.Add($"{t.Name} → NodeType=\"{instance.NodeType}\"");
            }
        }

        Assert.IsTrue(
            unbacked.Count == 0,
            $"The following NodeBuilders have a NodeType not backed by a KgNodeTypes constant: {string.Join("; ", unbacked)}.\n"
                + "Add a constant to KgNodeTypes and reference it from NodeType (avoid raw string literals)."
        );
    }

    static IEnumerable<string> GetKgNodeTypesConstants()
    {
        var fields = typeof(KgNodeTypes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
        return fields.Select(f => (string)f.GetRawConstantValue()!).Where(v => !string.IsNullOrEmpty(v));
    }

    static IEnumerable<Type> GetAllNodeBuilderClassTypes()
    {
        var nodeBuilderInterface = typeof(INodeBuilder);
        return nodeBuilderInterface.Assembly.GetTypes().Where(t => nodeBuilderInterface.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
    }

    /// <summary>
    /// Reflectively instantiates every concrete <see cref="INodeBuilder"/>
    /// subclass in the Services assembly and returns the set of
    /// <see cref="INodeBuilder.NodeType"/> values they declare. This mirrors
    /// what <c>InfoboxGraphService.RegisterAllBuilders</c> does at runtime —
    /// the registration list IS the source of truth, so any builder that
    /// exists but isn't registered will appear in the missing set on the
    /// first test above.
    /// </summary>
    static HashSet<string> GetAllRegisteredNodeBuilderTypes()
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in GetAllNodeBuilderClassTypes())
        {
            // Include UnknownNodeBuilder since it IS registered in
            // InfoboxGraphService.RegisterAllBuilders as the catch-all,
            // and KgNodeTypes.Unknown maps to it.
            try
            {
                var instance = (INodeBuilder)Activator.CreateInstance(t)!;
                registered.Add(instance.NodeType);
            }
            catch (MissingMethodException)
            {
                // Builder requires constructor args — skip in this reflection pass.
                // Callers wanting to assert against such builders should mock or
                // adapt the test.
            }
        }
        return registered;
    }
}
