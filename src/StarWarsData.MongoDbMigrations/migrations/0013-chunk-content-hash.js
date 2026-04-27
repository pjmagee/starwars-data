// Migration 0013: Backfill search.chunks.contentHash + index { contentHash: 1 }
//
// Phase A of Design-020 (async Holocron pipeline). The change-aware re-run
// feature requires a per-chunk content hash so the agent can skip chunks
// that haven't changed since the last enhancement of a given node. Going
// forward, ArticleChunkingService.ComputeContentHash populates this at
// chunk-write time; this migration backfills the existing 817K chunks.
//
// Hash: SHA256 hex (lowercase), computed in mongosh's Node-based crypto module.
//
// Idempotent: re-running rehashes the same text → same hash; createIndex
// returns ok if the index already exists.

const crypto = require("crypto");

globalThis.__currentMigration = {
  id: "0013-chunk-content-hash",
  description: "Backfill search.chunks.contentHash + index { contentHash: 1 }",

  up(db) {
    const chunks = db.getCollection("search.chunks");
    const total = chunks.countDocuments({});
    print(`    Backfilling contentHash on ${total} chunks…`);

    const start = Date.now();
    let processed = 0;
    let bulk = [];

    chunks.find({}, { _id: 1, text: 1 }).forEach((doc) => {
      const hash = crypto
        .createHash("sha256")
        .update(doc.text || "", "utf8")
        .digest("hex");
      bulk.push({ updateOne: { filter: { _id: doc._id }, update: { $set: { contentHash: hash } } } });
      if (bulk.length >= 1000) {
        chunks.bulkWrite(bulk, { ordered: false });
        processed += bulk.length;
        bulk = [];
        if (processed % 50000 === 0) {
          print(`      ${processed} / ${total} chunks…`);
        }
      }
    });
    if (bulk.length) {
      chunks.bulkWrite(bulk, { ordered: false });
      processed += bulk.length;
    }
    const elapsed = ((Date.now() - start) / 1000).toFixed(1);
    print(`    Updated ${processed} chunks in ${elapsed}s.`);

    print("    Creating index { contentHash: 1 }…");
    const indexName = chunks.createIndex({ contentHash: 1 }, { name: "contentHash_1" });
    print(`    Index '${indexName}' ready.`);

    return { processed, indexName };
  },
};
