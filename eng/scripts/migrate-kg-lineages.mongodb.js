// ============================================================================
// Migration: Populate lineage closures on kg.nodes
// ============================================================================
//
// Computes transitive closures for each HierarchyRegistry-registered lineage
// and embeds them on kg.nodes as lineages.<key>. Creates the wildcard index
// ix_lineages_wildcard for O(1) membership queries.
//
// See Design-008 for the full design rationale.
//
// Prerequisites:
//   - kg.nodes and kg.edges must both exist and be populated (Phase 5 output)
//
// Safe to run multiple times — clears lineages first, then re-populates.
// Uses the aggregation-pipeline form of $merge.whenMatched to $set individual
// lineage subfields without overwriting the parent lineages document, so each
// lineage pass is additive.
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-kg-lineages.mongodb.js
//
// Approximate runtime: ~20 seconds on dev (138k nodes, 595k edges).
// ============================================================================

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
db = db.getSiblingDB(DB_NAME);

const nodesCount = db["kg.nodes"].countDocuments();
const edgesCount = db["kg.edges"].countDocuments();
print(`kg.nodes: ${nodesCount}, kg.edges: ${edgesCount}`);

if (nodesCount === 0 || edgesCount === 0) {
    print("ERROR: kg.nodes or kg.edges is empty. Run Phase 5 ETL first.");
    quit(1);
}

// Registry matching HierarchyRegistry.cs — must stay in sync manually.
// [label, direction, lineageKey, description]
const LINEAGES = [
    ["apprentice_of", "forward", "apprentice_of", "Jedi/Sith master lineage"],
    ["child_of", "forward", "ancestors", "Biological ancestors"],
    ["in_system", "forward", "in_system", "Containing star system(s)"],
    ["in_sector", "forward", "in_sector", "Containing sector(s)"],
    ["in_region", "forward", "in_region", "Containing region(s)"],
    ["part_of", "forward", "part_of", "Parent publication"],
];

// Clear any existing lineages so we start clean
print("\nClearing existing lineages subdocument...");
db["kg.nodes"].updateMany({}, { $unset: { lineages: "" } });

for (const [label, dir, key, desc] of LINEAGES) {
    const t0 = Date.now();

    // $graphLookup direction:
    //   forward:  seed is fromId, walk to toId → connectFromField=toId, connectToField=fromId
    //   reverse:  seed is toId, walk to fromId → connectFromField=fromId, connectToField=toId
    const connectFrom = dir === "forward" ? "toId" : "fromId";
    const connectTo = dir === "forward" ? "fromId" : "toId";

    // The closure is the set of all unique target ids reachable from the seed.
    // $setUnion deduplicates (cycles produce repeated ids via different paths).
    const closureExpr =
        dir === "forward" ? "$_chain.toId" : "$_chain.fromId";

    db["kg.nodes"].aggregate([
        {
            $graphLookup: {
                from: "kg.edges",
                startWith: "$_id",
                connectFromField: connectFrom,
                connectToField: connectTo,
                as: "_chain",
                restrictSearchWithMatch: { label: label },
            },
        },
        // Only process nodes that have at least one edge in this lineage
        { $match: { "_chain.0": { $exists: true } } },
        {
            $project: {
                _id: 1,
                closure: { $setUnion: [closureExpr] },
            },
        },
        // Pipeline-form whenMatched: $$new references the incoming source doc
        // This $sets a specific subfield (lineages.<key>) without replacing the parent doc
        {
            $merge: {
                into: "kg.nodes",
                on: "_id",
                whenMatched: [
                    { $set: { ["lineages." + key]: "$$new.closure" } },
                ],
                whenNotMatched: "discard",
            },
        },
    ]).toArray();

    const elapsed = Date.now() - t0;
    const populated = db["kg.nodes"].countDocuments({
        ["lineages." + key]: { $exists: true },
    });
    print(
        `  ${key.padEnd(16)} → ${populated.toString().padStart(6)} nodes in ${elapsed}ms  (${desc})`
    );
}

// ── Create wildcard index ──

print("\nCreating ix_lineages_wildcard...");
try {
    db["kg.nodes"].createIndex(
        { "lineages.$**": 1 },
        { name: "ix_lineages_wildcard" }
    );
    print("  Created ix_lineages_wildcard");
} catch (e) {
    if (e.codeName === "IndexOptionsConflict" || e.code === 85) {
        print("  ix_lineages_wildcard already exists — skipping");
    } else {
        throw e;
    }
}

// ── Spot checks ──

print("\n=== Spot checks ===");

// Anakin's master lineage
const anakin = db["kg.nodes"].findOne(
    { _id: 452390 },
    { name: 1, "lineages.apprentice_of": 1 }
);
if (anakin?.lineages?.apprentice_of) {
    const names = db["kg.nodes"]
        .find(
            { _id: { $in: anakin.lineages.apprentice_of } },
            { name: 1 }
        )
        .toArray()
        .map((n) => n.name);
    print(
        `Anakin's master lineage (${names.length}): ${names.join(", ")}`
    );
} else {
    print("WARNING: Anakin (452390) has no apprentice_of lineage");
}

// Tatooine containment chain
const tat = db["kg.nodes"].findOne(
    { name: /^Tatooine$/i },
    { name: 1, lineages: 1 }
);
if (tat) {
    print(`Tatooine lineages:`);
    for (const k of Object.keys(tat.lineages ?? {})) {
        const names = db["kg.nodes"]
            .find({ _id: { $in: tat.lineages[k] } }, { name: 1 })
            .toArray()
            .map((n) => n.name);
        print(`  ${k}: [${names.join(", ")}]`);
    }
}

// Membership query: nodes in Core Worlds region
const core = db["kg.nodes"].findOne(
    { name: "Core Worlds" },
    { _id: 1 }
);
if (core) {
    const t0 = Date.now();
    const count = db["kg.nodes"].countDocuments({
        "lineages.in_region": core._id,
    });
    print(
        `Nodes in Core Worlds (in_region membership): ${count} in ${Date.now() - t0}ms`
    );
}

// Explain plan
if (core) {
    const plan = db["kg.nodes"]
        .find({ "lineages.in_region": core._id })
        .explain("executionStats");
    const stats = plan.executionStats;
    const idx =
        plan.queryPlanner.winningPlan?.inputStage?.indexName ?? "unknown";
    print(
        `Explain: index=${idx} keysExamined=${stats.totalKeysExamined} docsExamined=${stats.totalDocsExamined} nReturned=${stats.nReturned} timeMs=${stats.executionTimeMillis}`
    );
}

print("\n=== Lineage migration complete ===");
