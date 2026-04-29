// Migration 0017: Create kg.holocron_audits collection
//
// Per-proposal audit trail for the Holocron pipeline. One row per UNIQUE
// post-dedup proposal, with the outcome stamped at every pipeline stage so we
// can answer "why was proposal X rejected?" without re-running.
//
// Background: the v1.5.2 Anakin run produced 122 unique post-dedup proposals,
// 48 of which made it to kg.enrichments / kg.edge_enrichments. The other 74
// were silently dropped by various pre-flight rules and the LLM verifier with
// no per-proposal record retained — only summary counters on the job document.
// That's a black box: tuning hedge regex / verifier prompts / type validation
// is impossible without seeing what's actually being rejected.
//
// Schema lives in HolocronAudit.cs (StarWarsData.Models). Indexes here only.
//
// Idempotent: drops & recreates the collection's indexes if they already exist
// with mismatched options, otherwise no-op.

globalThis.__currentMigration = {
  id: "0017-create-holocron-audits-collection",
  description: "Create kg.holocron_audits collection + indexes for per-proposal pipeline audit trail",

  up(db) {
    const collectionName = "kg.holocron_audits";

    // Create collection if missing. createCollection is idempotent; it errors only
    // when called with conflicting options, which we don't pass.
    const existing = db.getCollectionNames().includes(collectionName);
    if (!existing) {
      db.createCollection(collectionName);
      print(`    Created collection ${collectionName}.`);
    } else {
      print(`    Collection ${collectionName} already exists.`);
    }

    const coll = db.getCollection(collectionName);

    // ── Indexes ─────────────────────────────────────────────────────────────
    // 1. (jobId, kind, fromId, toId, label, fieldPath) is the natural key for
    //    upserting from the Verifier and Apply executors. Sparse fields default
    //    to null when omitted; the index handles those uniformly.
    //    NOT UNIQUE — duplicates are possible across runs of the same job
    //    after a workflow restart. Deduplication is a Mongo aggregate concern
    //    in the audit-reading code, not an insert-time constraint. (If we made
    //    this unique we'd hit E11000 on legitimate restart-resume cases.)
    print("    Creating index on (jobId, pageId, kind)...");
    coll.createIndex(
      { jobId: 1, pageId: 1, kind: 1 },
      { name: "jobId_1_pageId_1_kind_1" },
    );

    // 2. Outcome filter — most common audit query is "show me all
    //    rejected_preflight_hedge for this run". Outcome alone has low
    //    cardinality but pairs cleanly with jobId for selectivity.
    print("    Creating index on (jobId, outcome)...");
    coll.createIndex(
      { jobId: 1, outcome: 1 },
      { name: "jobId_1_outcome_1" },
    );

    // 3. Per-page recent-runs query for the Frontend's "show me what was
    //    rejected on this node's last run" panel (future). Sort-friendly
    //    via createdAt descending.
    print("    Creating index on (pageId, createdAt)...");
    coll.createIndex(
      { pageId: 1, createdAt: -1 },
      { name: "pageId_1_createdAt_-1" },
    );

    print(`    ${collectionName} indexes ready.`);
    return { collection: collectionName, count: coll.countDocuments() };
  },
};
