// ============================================================================
// Migration: Dedup kg.edges and apply the consolidated named index set
// ============================================================================
//
// Removes duplicate (fromId, toId, label) edges that the pre-fix ETL produced,
// then creates the authoritative named index set from ADR-003 including the
// unique constraint.
//
// IMPORTANT: Run migrate-kg-edges-denorm.mongodb.js FIRST so the dedup
// prefers edges with richer data (temporal bounds, meta, higher weight).
//
// This script is DESTRUCTIVE — it replaces kg.edges via $out + rename.
// Take a backup or snapshot before running on prod.
//
// Safe to run if kg.edges already has no dupes — the dedup is a no-op and
// the index creation is idempotent (createIndex skips if spec+name match).
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-kg-edges-dedup-and-index.mongodb.js
//
// Approximate runtime: ~30 seconds on 600k edges.
// ============================================================================

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
db = db.getSiblingDB(DB_NAME);

const edges = db["kg.edges"];
const before = edges.countDocuments();
print(`kg.edges document count before dedup: ${before}`);

// ── Step 1: Check for duplicates ──

print("\n=== Step 1: Checking for duplicate (fromId, toId, label) tuples ===");

const dupeResult = edges
    .aggregate([
        {
            $group: {
                _id: { f: "$fromId", t: "$toId", l: "$label" },
                count: { $sum: 1 },
            },
        },
        { $match: { count: { $gt: 1 } } },
        { $count: "dupes" },
    ])
    .toArray();

const dupeCount = dupeResult.length > 0 ? dupeResult[0].dupes : 0;
print(`  Duplicate groups found: ${dupeCount}`);

if (dupeCount === 0) {
    print("  No duplicates — skipping dedup, proceeding to index creation.");
} else {
    // ── Step 2: Dedup via $out + rename ──

    print(
        `\n=== Step 2: Deduplicating ${dupeCount} groups via $out + rename ===`
    );
    print(
        "  Quality preference: explicit temporal bounds > meta > higher weight"
    );

    const t0 = Date.now();
    edges.aggregate([
        {
            $addFields: {
                _q: {
                    $add: [
                        {
                            $cond: [
                                { $ifNull: ["$fromYear", false] },
                                100,
                                0,
                            ],
                        },
                        {
                            $cond: [
                                { $ifNull: ["$meta", false] },
                                10,
                                0,
                            ],
                        },
                        { $ifNull: ["$weight", 0] },
                    ],
                },
            },
        },
        { $sort: { _q: -1 } },
        {
            $group: {
                _id: { f: "$fromId", t: "$toId", l: "$label" },
                best: { $first: "$$ROOT" },
            },
        },
        { $replaceRoot: { newRoot: "$best" } },
        { $unset: "_q" },
        { $out: "kg.edges.tmp_dedup" },
    ]).toArray();

    const afterCount = db["kg.edges.tmp_dedup"].countDocuments();
    print(
        `  Deduped: ${before} → ${afterCount} (removed ${before - afterCount} edges) in ${Date.now() - t0}ms`
    );

    // Verify zero dupes in the deduped copy
    const verifyDupes = db["kg.edges.tmp_dedup"]
        .aggregate([
            {
                $group: {
                    _id: { f: "$fromId", t: "$toId", l: "$label" },
                    count: { $sum: 1 },
                },
            },
            { $match: { count: { $gt: 1 } } },
            { $count: "dupes" },
        ])
        .toArray();
    const remaining =
        verifyDupes.length > 0 ? verifyDupes[0].dupes : 0;
    if (remaining > 0) {
        print(
            `  ERROR: ${remaining} duplicates remain after dedup! Aborting rename.`
        );
        db["kg.edges.tmp_dedup"].drop();
        quit(1);
    }
    print("  Verification: zero duplicates in deduped copy ✓");

    // Rename: replaces kg.edges with the clean copy (drops old + its indexes)
    db["kg.edges.tmp_dedup"].renameCollection("kg.edges", true);
    print("  Renamed kg.edges.tmp_dedup → kg.edges (dropTarget=true)");
}

// ── Step 3: Create the authoritative named index set ──

print("\n=== Step 3: Creating authoritative named index set (ADR-003) ===");

const c = db["kg.edges"];

// createIndex is idempotent when name + spec match, so safe to re-run
c.createIndex(
    { fromId: 1, toId: 1, label: 1 },
    { name: "ix_fromId_toId_label", unique: true }
);
c.createIndex({ fromId: 1, label: 1 }, { name: "ix_fromId_label" });
c.createIndex({ toId: 1, label: 1 }, { name: "ix_toId_label" });
c.createIndex({ label: 1 }, { name: "ix_label" });
c.createIndex({ continuity: 1 }, { name: "ix_continuity" });
c.createIndex({ sourcePageId: 1 }, { name: "ix_sourcePageId" });
c.createIndex({ pairId: 1 }, { name: "ix_pairId", sparse: true });

print("  Indexes:");
c.getIndexes().forEach((i) =>
    print(
        `    ${i.name} ${JSON.stringify(i.key)}${i.unique ? " [UNIQUE]" : ""}${i.sparse ? " [SPARSE]" : ""}`
    )
);

const after = c.countDocuments();
print(`\n=== Complete: ${after} clean edges with ${c.getIndexes().length} indexes ===`);
