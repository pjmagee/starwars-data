// Migration 0001: Drop stale pairId field from kg.edges
//
// Prod edges carry pairId: null on all rows from the pre-Phase-5 ETL.
// Phase 5 full-insert won't carry this field (BsonIgnoreIfNull), but if
// validators run before Phase 5 completes, the field causes noise.
// Idempotent: updateMany with $unset is a no-op when the field is absent.

globalThis.__currentMigration = {
  id: "0001-drop-stale-pairid",
  description: "Drop stale pairId field from kg.edges (pre-Phase 5 cleanup)",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const count = edges.countDocuments({ pairId: { $exists: true } });

    if (count === 0) {
      print("    No edges have pairId — nothing to do.");
      return { modifiedCount: 0 };
    }

    print(`    ${count} edges have stale pairId — unsetting...`);
    const result = edges.updateMany(
      { pairId: { $exists: true } },
      { $unset: { pairId: "" } }
    );
    print(`    Unset pairId on ${result.modifiedCount} edges.`);
    return { modifiedCount: result.modifiedCount };
  },
};
