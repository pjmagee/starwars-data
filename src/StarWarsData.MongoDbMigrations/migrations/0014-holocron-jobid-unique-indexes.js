// Migration 0014: Per-job uniqueness on Holocron enrichment collections
//
// Adds three partial unique indexes that make the async pipeline's Apply stage
// idempotent under crash-and-resume (Design-020 risk #4 — "Apply isn't strictly
// idempotent"). If the API process dies after Apply has inserted some rows but
// before the workflow transitions the job to Completed, the resumed run will
// re-enter Apply with the same consolidated proposal set. Without these indexes,
// the second attempt would insert duplicate rows.
//
// The partial filter `{jobId: {$exists: true, $ne: ""}}` exempts pre-Phase-B
// rows from the legacy synchronous path (HolocronAgent.EnhanceNodeAsync /
// scheduled daily-pass) which never wrote a `jobId` field. Those rows can
// duplicate freely — they're outside the per-job invariant.
//
// HolocronApplyExecutor catches BulkWriteException, filters to E11000 codes, and
// counts duplicates as silent successes. So Apply replays no-op cleanly without
// breaking the run.
//
// Idempotent: createIndex returns ok if the index already exists with the same
// keys + options.

globalThis.__currentMigration = {
  id: "0014-holocron-jobid-unique-indexes",
  description:
    "Partial unique indexes on (jobId, identity-fields) for kg.enrichments / kg.edge_enrichments / kg.events (Design-020)",

  up(db) {
    const results = {};

    // MongoDB partial filter expressions support a limited operator set: $exists,
    // $eq, $gt/$gte/$lt/$lte, $type, $and, $or — NOT $ne. So we express
    // "non-empty string" as "is a string AND lexicographically greater than empty"
    // (`$gt: ""`). Missing-field and empty-string rows are both excluded, which is
    // exactly the legacy-row exemption we want.
    const partialFilter = { jobId: { $type: "string", $gt: "" } };

    // ── kg.enrichments — unique within a job per (pageId, fieldPath) ────
    print("    Creating partial unique index on kg.enrichments...");
    results.nodeEnrichments = db.getCollection("kg.enrichments").createIndex(
      { jobId: 1, pageId: 1, fieldPath: 1 },
      {
        name: "jobId_1_pageId_1_fieldPath_1_unique",
        unique: true,
        partialFilterExpression: partialFilter,
      },
    );

    // ── kg.edge_enrichments — unique within a job per (fromId, toId, label) ──
    print("    Creating partial unique index on kg.edge_enrichments...");
    results.edgeEnrichments = db.getCollection("kg.edge_enrichments").createIndex(
      { jobId: 1, fromId: 1, toId: 1, label: 1 },
      {
        name: "jobId_1_fromId_1_toId_1_label_1_unique",
        unique: true,
        partialFilterExpression: partialFilter,
      },
    );

    // ── kg.events — unique within a job per enrichmentId ────────────────
    // Only enrichment-creation events have an enrichmentId, so the partial
    // filter narrows further to those. PassStarted / PassCompleted / staleness
    // events lack enrichmentId and are exempt — they can multi-emit without harm.
    print("    Creating partial unique index on kg.events...");
    results.events = db.getCollection("kg.events").createIndex(
      { jobId: 1, enrichmentId: 1 },
      {
        name: "jobId_1_enrichmentId_1_unique",
        unique: true,
        partialFilterExpression: {
          jobId: { $type: "string", $gt: "" },
          enrichmentId: { $type: "objectId" },
        },
      },
    );

    print(
      `    Indexes ready: ${results.nodeEnrichments}, ${results.edgeEnrichments}, ${results.events}`,
    );

    return results;
  },
};
