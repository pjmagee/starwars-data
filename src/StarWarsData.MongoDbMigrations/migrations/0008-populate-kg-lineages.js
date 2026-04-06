// Migration 0008: Populate lineage closures on kg.nodes
//
// Computes transitive closures for each HierarchyRegistry-registered lineage
// and embeds them on kg.nodes as lineages.<key>. Creates the wildcard index
// ix_lineages_wildcard for O(1) membership queries. See Design-008.
//
// Prerequisites: kg.nodes and kg.edges must be populated (Phase 5 output).
// Idempotent: clears lineages first, then re-populates.
// Must stay in sync with HierarchyRegistry.cs
// [label, direction, lineageKey, description]
const LINEAGES = [
  ["apprentice_of", "forward", "apprentice_of", "Jedi/Sith master lineage"],
  ["child_of",      "forward", "ancestors",      "Biological ancestors"],
  ["in_system",     "forward", "in_system",      "Containing star system(s)"],
  ["in_sector",     "forward", "in_sector",      "Containing sector(s)"],
  ["in_region",     "forward", "in_region",      "Containing region(s)"],
  ["part_of",       "forward", "part_of",        "Parent publication"],
];

globalThis.__currentMigration = {
  id: "0008-populate-kg-lineages",
  description: "Compute lineage transitive closures on kg.nodes + create wildcard index (Design-008)",

  up(db) {
    const nodes = db.getCollection("kg.nodes");
    const edges = db.getCollection("kg.edges");
    const nodesCount = nodes.countDocuments();
    const edgesCount = edges.countDocuments();

    if (nodesCount === 0 || edgesCount === 0) {
      throw new Error("kg.nodes or kg.edges is empty. Run Phase 5 ETL first.");
    }

    print(`    kg.nodes: ${nodesCount}, kg.edges: ${edgesCount}`);

    // Clear existing lineages
    print("    Clearing existing lineages...");
    nodes.updateMany({}, { $unset: { lineages: "" } });

    const results = {};

    for (const [label, dir, key, desc] of LINEAGES) {
      const t0 = Date.now();

      // $graphLookup direction:
      //   forward: seed=fromId, walk to toId
      const connectFrom = dir === "forward" ? "toId" : "fromId";
      const connectTo = dir === "forward" ? "fromId" : "toId";
      const closureExpr = dir === "forward" ? "$_chain.toId" : "$_chain.fromId";

      nodes.aggregate([
        {
          $graphLookup: {
            from: "kg.edges",
            startWith: "$_id",
            connectFromField: connectFrom,
            connectToField: connectTo,
            as: "_chain",
            restrictSearchWithMatch: { label },
          },
        },
        { $match: { "_chain.0": { $exists: true } } },
        { $project: { _id: 1, closure: { $setUnion: [closureExpr] } } },
        {
          $merge: {
            into: "kg.nodes",
            on: "_id",
            whenMatched: [{ $set: { ["lineages." + key]: "$$new.closure" } }],
            whenNotMatched: "discard",
          },
        },
      ]).toArray();

      const elapsed = Date.now() - t0;
      const populated = nodes.countDocuments({ ["lineages." + key]: { $exists: true } });
      print(`    ${key.padEnd(16)} -> ${String(populated).padStart(6)} nodes in ${elapsed}ms  (${desc})`);
      results[key] = { populated, elapsed };
    }

    // Create wildcard index
    print("    Creating ix_lineages_wildcard...");
    try {
      nodes.createIndex({ "lineages.$**": 1 }, { name: "ix_lineages_wildcard" });
      print("    Created ix_lineages_wildcard.");
    } catch (e) {
      if (e.codeName === "IndexOptionsConflict" || e.code === 85) {
        print("    ix_lineages_wildcard already exists — skipping.");
      } else {
        throw e;
      }
    }

    return results;
  },
};
