// ============================================================================
// Migration: Create the kg.edges.bidir bidirectional view
// ============================================================================
//
// Creates (or replaces) the kg.edges.bidir view that exposes each edge twice:
// once as-stored (forward branch) and once flipped with the label replaced by
// its reverseLabel (reverse branch). See Design-007.
//
// Prerequisites:
//   - kg.edges must have `reverseLabel` populated (run migrate-kg-edges-denorm.mongodb.js first)
//
// Safe to run multiple times (drops + recreates the view).
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-kg-bidir-view.mongodb.js
//
// Approximate runtime: instant (view is lazily evaluated).
// ============================================================================

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
db = db.getSiblingDB(DB_NAME);

// Verify reverseLabel is populated
const revCount = db["kg.edges"].countDocuments({
    reverseLabel: { $exists: true },
});
const totalCount = db["kg.edges"].countDocuments();
print(
    `kg.edges: ${revCount}/${totalCount} edges have reverseLabel (${((revCount / totalCount) * 100).toFixed(1)}%)`
);

if (revCount === 0) {
    print(
        "ERROR: No edges have reverseLabel populated. Run migrate-kg-edges-denorm.mongodb.js first."
    );
    quit(1);
}

// Drop existing view if present
try {
    db["kg.edges.bidir"].drop();
    print("Dropped existing kg.edges.bidir");
} catch (e) {
    // View didn't exist — fine
}

// Create the view matching InfoboxGraphService.EnsureBidirectionalEdgesViewAsync
db.createView("kg.edges.bidir", "kg.edges", [
    // Forward branch: every edge as-stored, annotated with direction="forward"
    { $addFields: { direction: "forward" } },
    // Reverse branch: $unionWith another scan of kg.edges with fields flipped
    {
        $unionWith: {
            coll: "kg.edges",
            pipeline: [
                // Drop edges without a reverse label — they're one-way
                {
                    $match: {
                        reverseLabel: {
                            $exists: true,
                            $nin: [null, ""],
                        },
                    },
                },
                {
                    $project: {
                        _id: 1,
                        fromId: "$toId",
                        toId: "$fromId",
                        fromName: "$toName",
                        toName: "$fromName",
                        fromType: "$toType",
                        toType: "$fromType",
                        fromRealm: "$toRealm",
                        toRealm: "$fromRealm",
                        label: "$reverseLabel",
                        weight: 1,
                        evidence: 1,
                        continuity: 1,
                        fromYear: 1,
                        toYear: 1,
                        sourcePageId: 1,
                        direction: { $literal: "reverse" },
                    },
                },
            ],
        },
    },
]);

// Verify
const viewCount = db["kg.edges.bidir"]
    .aggregate([{ $count: "n" }])
    .toArray()[0].n;
print(
    `\nCreated kg.edges.bidir: ${viewCount} total documents (${totalCount} forward + ${viewCount - totalCount} reverse)`
);

// Spot check
const sample = db["kg.edges.bidir"]
    .aggregate([
        { $match: { fromId: 452390 } }, // Anakin Skywalker
        { $limit: 5 },
        {
            $project: {
                fromName: 1,
                toName: 1,
                label: 1,
                direction: 1,
            },
        },
    ])
    .toArray();
print("\nSample edges from Anakin Skywalker (452390):");
sample.forEach((e) =>
    print(
        `  [${e.direction}] ${e.fromName} —${e.label}→ ${e.toName}`
    )
);

print("\n=== View creation complete ===");
