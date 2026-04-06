// Migration 0006: Dedup kg.edges and create authoritative named index set
//
// Removes duplicate (fromId, toId, label) edges produced by the pre-fix ETL,
// then creates the named index set from ADR-003 including the unique constraint.
//
// DESTRUCTIVE if duplicates exist: replaces kg.edges via $out + rename.
// If no duplicates, only index creation runs (idempotent).
//
// Prerequisites: run 0005 (denorm) first so dedup prefers richer edges.
globalThis.__currentMigration = {
  id: "0006-dedup-kg-edges-and-indexes",
  description: "Dedup kg.edges on (fromId, toId, label) and create authoritative index set (ADR-003)",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const before = edges.countDocuments();
    print(`    kg.edges count before: ${before}`);

    // ── Step 1: Check for duplicates ──
    print("    Checking for duplicate (fromId, toId, label) tuples...");
    const dupeResult = edges.aggregate([
      { $group: { _id: { f: "$fromId", t: "$toId", l: "$label" }, count: { $sum: 1 } } },
      { $match: { count: { $gt: 1 } } },
      { $count: "dupes" },
    ]).toArray();

    const dupeCount = dupeResult.length > 0 ? dupeResult[0].dupes : 0;

    if (dupeCount === 0) {
      print("    No duplicates — skipping dedup.");
    } else {
      // ── Step 2: Dedup via $out + rename ──
      print(`    ${dupeCount} duplicate groups — deduplicating...`);
      print("    Quality preference: explicit temporal bounds > meta > higher weight");

      const t0 = Date.now();
      edges.aggregate([
        {
          $addFields: {
            _q: {
              $add: [
                { $cond: [{ $ifNull: ["$fromYear", false] }, 100, 0] },
                { $cond: [{ $ifNull: ["$meta", false] }, 10, 0] },
                { $ifNull: ["$weight", 0] },
              ],
            },
          },
        },
        { $sort: { _q: -1 } },
        { $group: { _id: { f: "$fromId", t: "$toId", l: "$label" }, best: { $first: "$$ROOT" } } },
        { $replaceRoot: { newRoot: "$best" } },
        { $unset: "_q" },
        { $out: "kg.edges.tmp_dedup" },
      ]).toArray();

      const afterCount = db.getCollection("kg.edges.tmp_dedup").countDocuments();
      print(`    Deduped: ${before} -> ${afterCount} (removed ${before - afterCount}) in ${Date.now() - t0}ms`);

      // Verify zero dupes in deduped copy
      const verifyDupes = db.getCollection("kg.edges.tmp_dedup").aggregate([
        { $group: { _id: { f: "$fromId", t: "$toId", l: "$label" }, count: { $sum: 1 } } },
        { $match: { count: { $gt: 1 } } },
        { $count: "dupes" },
      ]).toArray();

      const remaining = verifyDupes.length > 0 ? verifyDupes[0].dupes : 0;
      if (remaining > 0) {
        db.getCollection("kg.edges.tmp_dedup").drop();
        throw new Error(`${remaining} duplicates remain after dedup — aborting.`);
      }
      print("    Verification: zero duplicates in deduped copy.");

      db.getCollection("kg.edges.tmp_dedup").renameCollection("kg.edges", true);
      print("    Renamed kg.edges.tmp_dedup -> kg.edges (dropTarget=true).");
    }

    // ── Step 3: Authoritative named index set ──
    print("    Creating authoritative named index set (ADR-003)...");

    const c = db.getCollection("kg.edges");
    c.createIndex({ fromId: 1, toId: 1, label: 1 }, { name: "ix_fromId_toId_label", unique: true });
    c.createIndex({ fromId: 1, label: 1 }, { name: "ix_fromId_label" });
    c.createIndex({ toId: 1, label: 1 }, { name: "ix_toId_label" });
    c.createIndex({ label: 1 }, { name: "ix_label" });
    c.createIndex({ continuity: 1 }, { name: "ix_continuity" });
    c.createIndex({ sourcePageId: 1 }, { name: "ix_sourcePageId" });
    c.createIndex({ pairId: 1 }, { name: "ix_pairId", sparse: true });

    const indexes = c.getIndexes();
    print("    Indexes:");
    indexes.forEach(i => {
      const flags = [i.unique && "UNIQUE", i.sparse && "SPARSE"].filter(Boolean).join(", ");
      print(`      ${i.name} ${JSON.stringify(i.key)}${flags ? ` [${flags}]` : ""}`);
    });

    const after = c.countDocuments();
    return { before, after, removed: before - after, dupeGroups: dupeCount, indexCount: indexes.length };
  },
};
