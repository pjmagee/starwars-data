namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// Registry of tree- or DAG-shaped relationship labels whose transitive closures are
/// precomputed and embedded on <c>kg.nodes</c> as <c>lineages.&lt;label&gt;</c> ancestor
/// arrays during Phase 5 ETL.
///
/// This is the implementation of <b>ADR-003 Gap 1</b> (hierarchy helpers) and follows
/// MongoDB's <a href="https://www.mongodb.com/docs/manual/applications/data-models-tree-structures/">
/// tree structures modelling guidance</a>: materialised ancestor arrays for subgraphs
/// that are genuinely tree- or DAG-shaped turn N-hop traversal into a single-field read.
///
/// Adding a new entry: verify the label is genuinely tree/DAG by checking
/// <list type="bullet">
///   <item>Low fan-in on the target side (each node has at most a few "parents" in the lineage sense)</item>
///   <item>Bounded closure depth — typical chain length under ~20</item>
///   <item>No pathological cycles (small-cycle count is fine; the closure algorithm uses a visited set)</item>
/// </list>
/// Self-reversing labels like <c>sibling_of</c> form cliques, not trees, and MUST NOT be added.
/// </summary>
internal static class HierarchyRegistry
{
    /// <summary>
    /// Each entry defines one precomputed lineage. The closure is the set of all nodes
    /// reachable from a seed by walking edges labelled <see cref="Label"/> in the
    /// <see cref="Direction"/> given.
    ///
    /// <para>
    /// Direction semantics match <c>FieldSemantics.Relationships</c> conventions:
    /// <list type="bullet">
    ///   <item><b>Forward</b>: seed is the edge's <c>fromId</c> on the first hop; next seed is <c>toId</c>.</item>
    ///   <item><b>Reverse</b>: seed is the edge's <c>toId</c> on the first hop; next seed is <c>fromId</c>.</item>
    /// </list>
    /// Pick whichever direction produces the "ancestors" the caller cares about. For
    /// <c>apprentice_of</c> (apprentice → master), forward gives the chain of masters.
    /// For <c>child_of</c> (child → parent), forward gives the chain of ancestors.
    /// </para>
    /// </summary>
    public sealed record LineageDefinition(string Label, LineageDirection Direction, string LineageKey, string Description);

    public enum LineageDirection
    {
        Forward,
        Reverse,
    }

    /// <summary>
    /// The registered lineages, surveyed against <c>starwars-dev</c> on 2026-04-05
    /// for coverage and tree-shape validation. See ADR-003 Gap 1 for the survey results.
    /// </summary>
    public static readonly IReadOnlyList<LineageDefinition> Lineages =
    [
        // Jedi / Sith master lineage. 1,531 edges on dev; two direct cycles exist
        // (characters who trained each other at different times) — handled by the
        // BFS visited set in the closure computation.
        new("apprentice_of", LineageDirection.Forward, "apprentice_of", "Masters in order from direct master to grand-masters"),
        // Family tree. 1,905 edges on dev. Using child_of (forward = walk towards parents)
        // rather than parent_of to avoid double-counting — the two labels are mutual
        // reverses in FieldSemantics.Relationships and Phase 6 writes both directions.
        new("child_of", LineageDirection.Forward, "ancestors", "Biological ancestors (DAG, up to 2 parents per generation)"),
        // Planetary containment chain. Three stacked labels because Wookieepedia infoboxes
        // link planets directly to whichever level (region, sector, or system) has data.
        // These are computed as separate closures; a planet in a well-documented system
        // will have non-empty lineages in all three dimensions, while a planet that only
        // links to its region will have only `in_region`.
        new("in_system", LineageDirection.Forward, "in_system", "Containing star system(s)"),
        new("in_sector", LineageDirection.Forward, "in_sector", "Containing sector(s)"),
        new("in_region", LineageDirection.Forward, "in_region", "Containing region(s)"),
        // Publication hierarchy: individual comic issues → series. 132 edges on dev.
        // Included because it's genuinely tree-shaped and demonstrates the pattern
        // extends beyond in-universe data.
        new("part_of", LineageDirection.Forward, "part_of", "Parent publication / containing work"),
    ];
}
