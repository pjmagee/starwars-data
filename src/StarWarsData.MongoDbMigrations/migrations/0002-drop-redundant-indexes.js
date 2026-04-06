// Migration 0002: Drop redundant standalone indexes on kg.edges
//
// Prod has old-shape indexes (fromId_1, toId_1 standalone) that are now
// covered by compound prefixes (ix_fromId_label, ix_toId_label, etc.).
// These waste write overhead. Drop them so Phase 5 starts clean.
// Idempotent: skips indexes that are already absent.
globalThis.__currentMigration = {
  id: "0002-drop-redundant-indexes",
  description: "Drop redundant standalone indexes on kg.edges (fromId_1, toId_1)",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const current = new Set(edges.getIndexes().map(i => i.name));
    const stale = ["fromId_1", "toId_1"];
    const dropped = [];

    for (const name of stale) {
      if (current.has(name)) {
        edges.dropIndex(name);
        print(`    Dropped index: ${name}`);
        dropped.push(name);
      } else {
        print(`    Index ${name} already absent — skip.`);
      }
    }

    return { dropped };
  },
};
