// ============================================================================
// Migration: Denormalize fromRealm, toRealm, reverseLabel onto kg.edges
// ============================================================================
//
// Backfills the three new denormalized fields on kg.edges that were added in
// ADR-003. These fields are populated natively by the Phase 5 ETL on every
// full rebuild, but this script applies them to an EXISTING dataset without
// requiring a full Phase 5 rerun.
//
// Safe to run multiple times (idempotent — $merge with whenMatched: merge).
// Safe to run on a live database (no deletes, no renames).
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-kg-edges-denorm.mongodb.js
//
// Prerequisites:
//   - kg.nodes must exist and have `realm` populated (Phase 5 already does this)
//   - kg.labels must exist (Phase 5 already populates this via BuildLabelRegistryAsync)
//
// What it does:
//   1. Backfills fromRealm on edges by $lookup-ing the source node's realm
//   2. Backfills toRealm on edges by $lookup-ing the target node's realm
//   3. Backfills reverseLabel on edges by $lookup-ing kg.labels for the reverse form
//
// Approximate runtime: ~30-60 seconds on 600k edges (observed 2.4s per $lookup+$merge pass on dev).
// ============================================================================

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
db = db.getSiblingDB(DB_NAME);

const edges = db["kg.edges"];
const edgeCount = edges.countDocuments();
print(`kg.edges document count: ${edgeCount}`);

if (edgeCount === 0) {
    print("ERROR: kg.edges is empty — nothing to migrate. Run Phase 5 ETL first.");
    quit(1);
}

// ── Step 1: Backfill fromRealm + toRealm ──

print("\n=== Step 1: Backfilling fromRealm and toRealm from kg.nodes ===");

const t0 = Date.now();
edges.aggregate([
    {
        $lookup: {
            from: "kg.nodes",
            localField: "fromId",
            foreignField: "_id",
            as: "_f",
            pipeline: [{ $project: { realm: 1, _id: 0 } }],
        },
    },
    {
        $lookup: {
            from: "kg.nodes",
            localField: "toId",
            foreignField: "_id",
            as: "_t",
            pipeline: [{ $project: { realm: 1, _id: 0 } }],
        },
    },
    {
        $set: {
            fromRealm: {
                $ifNull: [{ $arrayElemAt: ["$_f.realm", 0] }, "Unknown"],
            },
            toRealm: {
                $ifNull: [{ $arrayElemAt: ["$_t.realm", 0] }, "Unknown"],
            },
        },
    },
    { $unset: ["_f", "_t"] },
    {
        $merge: {
            into: "kg.edges",
            on: "_id",
            whenMatched: "merge",
            whenNotMatched: "discard",
        },
    },
]).toArray();

const realmCount = edges.countDocuments({ fromRealm: { $exists: true } });
print(`  fromRealm/toRealm populated on ${realmCount} edges in ${Date.now() - t0}ms`);

// Distribution check
const realmDist = edges
    .aggregate([
        {
            $group: {
                _id: { fromRealm: "$fromRealm", toRealm: "$toRealm" },
                count: { $sum: 1 },
            },
        },
        { $sort: { count: -1 } },
    ])
    .toArray();
print("  Realm distribution:");
realmDist.forEach((r) =>
    print(
        `    ${r._id.fromRealm} → ${r._id.toRealm}: ${r.count}`
    )
);

// ── Step 2: Backfill reverseLabel ──

print("\n=== Step 2: Backfilling reverseLabel from kg.labels ===");

const labelsCount = db["kg.labels"].countDocuments();
if (labelsCount === 0) {
    print(
        "WARNING: kg.labels is empty. reverseLabel will not be populated."
    );
    print(
        "  Run Phase 5 ETL or BuildLabelRegistryAsync first, then re-run this script."
    );
} else {
    const t1 = Date.now();
    edges.aggregate([
        {
            $lookup: {
                from: "kg.labels",
                localField: "label",
                foreignField: "_id",
                as: "_l",
                pipeline: [{ $project: { reverse: 1, _id: 0 } }],
            },
        },
        {
            $addFields: {
                reverseLabel: {
                    $let: {
                        vars: {
                            rev: { $arrayElemAt: ["$_l.reverse", 0] },
                        },
                        in: {
                            $cond: [
                                {
                                    $and: [
                                        { $ne: ["$$rev", null] },
                                        { $ne: ["$$rev", ""] },
                                    ],
                                },
                                "$$rev",
                                "$$REMOVE",
                            ],
                        },
                    },
                },
            },
        },
        { $unset: "_l" },
        {
            $merge: {
                into: "kg.edges",
                on: "_id",
                whenMatched: "merge",
            },
        },
    ]).toArray();

    const revCount = edges.countDocuments({
        reverseLabel: { $exists: true },
    });
    print(
        `  reverseLabel populated on ${revCount}/${edgeCount} edges in ${Date.now() - t1}ms`
    );
    print(
        `  (${edgeCount - revCount} edges have no registered reverse — these are one-way labels)`
    );
}

print("\n=== Migration complete ===");
print(
    "Next steps:"
);
print(
    "  1. Run migrate-kg-edges-dedup-and-index.mongodb.js to dedup + apply indexes"
);
print(
    "  2. Run migrate-kg-bidir-view.mongodb.js to create the bidirectional view"
);
print(
    "  3. Run migrate-kg-lineages.mongodb.js to populate lineage closures"
);
