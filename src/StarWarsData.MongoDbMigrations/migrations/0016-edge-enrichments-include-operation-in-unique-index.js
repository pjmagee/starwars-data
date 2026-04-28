// Migration 0016: Add `operation` to the kg.edge_enrichments per-job unique index
//
// Migration 0014 created `jobId_1_fromId_1_toId_1_label_1_unique` as a partial
// unique index to make Apply idempotent under crash-and-resume. It missed a case:
// a single Holocron run can legitimately produce TWO enrichments on the same edge
// — an Annotate (role/qualifier/description) AND a FillGap (refined temporal
// bounds) — they encode different facets of the same edge.
//
// Without `operation` in the index, these collide on (jobId, fromId, toId, label)
// and the second insert in `BulkInsertTolerantAsync` is silently swallowed as
// E11000 duplicate-key. Symptom: consolidator reports 9 survivors, Apply log says
// 9 written, but only 6 land in kg.edge_enrichments — and the 3 missing are
// always the FillGaps (Apply iteration order Annotate → FillGap → Add → NodeProp
// means Annotate wins the race).
//
// Fix: drop the v0014 index and recreate with `operation` appended. The
// idempotency invariant becomes "within one run, at most one enrichment per
// (edge, operation)" which matches the consolidator's actual dedup keying.
//
// Idempotent: drops only if present, then creates the new index. Re-runs are no-ops.

globalThis.__currentMigration = {
  id: "0016-edge-enrichments-include-operation-in-unique-index",
  description:
    "Recreate kg.edge_enrichments partial unique index to include `operation` so Annotate + FillGap on the same edge can coexist within one job (Design-021 fallout)",

  up(db) {
    const coll = db.getCollection("kg.edge_enrichments");
    const oldName = "jobId_1_fromId_1_toId_1_label_1_unique";
    const newName = "jobId_1_fromId_1_toId_1_label_1_operation_1_unique";

    const indexes = coll.getIndexes();
    const hasOld = indexes.some((i) => i.name === oldName);
    const hasNew = indexes.some((i) => i.name === newName);

    if (hasOld) {
      print(`    Dropping legacy index ${oldName}...`);
      coll.dropIndex(oldName);
    } else {
      print(`    Legacy index ${oldName} not present (probably already migrated).`);
    }

    if (hasNew) {
      print(`    New index ${newName} already exists — leaving in place.`);
      return { dropped: hasOld, created: false };
    }

    print(`    Creating ${newName}...`);
    const partialFilter = { jobId: { $type: "string", $gt: "" } };
    const indexName = coll.createIndex(
      { jobId: 1, fromId: 1, toId: 1, label: 1, operation: 1 },
      {
        name: newName,
        unique: true,
        partialFilterExpression: partialFilter,
      },
    );
    print(`    Index ready: ${indexName}`);
    return { dropped: hasOld, created: true, indexName };
  },
};
