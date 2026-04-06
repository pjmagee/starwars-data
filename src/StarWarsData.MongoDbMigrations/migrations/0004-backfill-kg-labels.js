// Migration 0004: Backfill kg.labels as materialized view over kg.edges
//
// Mirrors InfoboxGraphService.BuildLabelRegistryAsync but runs directly in
// MongoDB so we don't need a full Phase 5 ETL just to populate the label
// registry. Uses $merge to upsert — existing labels with populated `reverse`
// and `description` fields (from Phase 5 FieldSemantics) are preserved.
//
// Idempotent: $merge upserts new labels and updates usage counts on existing
// ones without overwriting reverse/description populated by Phase 5.
globalThis.__currentMigration = {
  id: "0004-backfill-kg-labels",
  description: "Backfill kg.labels from kg.edges (materialized label registry, preserves Phase 5 data)",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const labels = db.getCollection("kg.labels");
    const edgeCount = edges.estimatedDocumentCount();

    if (edgeCount === 0) {
      print("    kg.edges is empty — skipping label backfill.");
      return { labelCount: 0, skipped: true };
    }

    const existingCount = labels.estimatedDocumentCount();
    const enrichedCount = labels.countDocuments({ reverse: { $nin: [null, ""] } });

    if (existingCount > 0 && enrichedCount > 0) {
      print(`    kg.labels already has ${existingCount} labels (${enrichedCount} with reverse from Phase 5).`);
      print("    Using $merge to update usage counts without overwriting reverse/description.");
    } else {
      print("    Aggregating kg.edges into kg.labels...");
    }

    const now = new Date();

    // Use a pipeline-form $merge that only updates usageCount, fromTypes,
    // toTypes, and createdAt — leaving reverse and description untouched
    // if they already have values from Phase 5 FieldSemantics.
    edges.aggregate([
      {
        $group: {
          _id: "$label",
          usageCount: { $sum: 1 },
          fromTypes: { $addToSet: "$fromType" },
          toTypes: { $addToSet: "$toType" },
        },
      },
      {
        $project: {
          _id: 1,
          fromTypes: {
            $filter: {
              input: { $sortArray: { input: "$fromTypes", sortBy: 1 } },
              cond: { $ne: ["$$this", ""] },
            },
          },
          toTypes: {
            $filter: {
              input: { $sortArray: { input: "$toTypes", sortBy: 1 } },
              cond: { $ne: ["$$this", ""] },
            },
          },
          usageCount: 1,
          createdAt: { $literal: now },
        },
      },
      {
        $merge: {
          into: "kg.labels",
          on: "_id",
          whenMatched: [
            {
              $set: {
                usageCount: "$$new.usageCount",
                fromTypes: "$$new.fromTypes",
                toTypes: "$$new.toTypes",
                createdAt: "$$new.createdAt",
                // Preserve existing reverse/description if non-empty;
                // set empty defaults only if absent
                reverse: {
                  $cond: [
                    { $and: [{ $ne: ["$reverse", null] }, { $ne: ["$reverse", ""] }] },
                    "$reverse",
                    { $ifNull: ["$$new.reverse", ""] },
                  ],
                },
                description: {
                  $cond: [
                    { $and: [{ $ne: ["$description", null] }, { $ne: ["$description", ""] }] },
                    "$description",
                    { $ifNull: ["$$new.description", ""] },
                  ],
                },
              },
            },
          ],
          whenNotMatched: "insert",
        },
      },
    ]).toArray();

    labels.createIndex({ usageCount: -1 }, { name: "ix_usageCount" });
    labels.createIndex({ fromTypes: 1 }, { name: "ix_fromTypes" });
    labels.createIndex({ toTypes: 1 }, { name: "ix_toTypes" });

    const labelCount = labels.countDocuments({});
    const afterEnriched = labels.countDocuments({ reverse: { $nin: [null, ""] } });
    print(`    kg.labels: ${labelCount} total, ${afterEnriched} with reverse.`);

    const top = labels.find({}).sort({ usageCount: -1 }).limit(5).toArray();
    print("    Top 5 by usage:");
    top.forEach(l => print(`      ${String(l._id).padEnd(25)} ${String(l.usageCount).padStart(7)}`));

    return { labelCount, enrichedPreserved: afterEnriched };
  },
};
