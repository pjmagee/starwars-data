// Migration 0005: Denormalize fromRealm, toRealm, reverseLabel onto kg.edges
//
// Backfills the three denormalized fields added in ADR-003. Phase 5 ETL
// populates these natively on full rebuilds; this script patches an
// existing dataset without requiring Phase 5.
//
// Idempotent: uses $merge with whenMatched: "merge" (additive, no deletes).
//
// Prerequisites:
//   - kg.nodes must have `realm` populated
//   - kg.labels should exist (0004 handles this)
globalThis.__currentMigration = {
  id: "0005-denorm-kg-edges",
  description: "Backfill fromRealm, toRealm, reverseLabel on kg.edges from kg.nodes + kg.labels",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const edgeCount = edges.countDocuments();

    if (edgeCount === 0) {
      print("    kg.edges is empty — nothing to migrate.");
      return { realmCount: 0, reverseLabelCount: 0 };
    }

    // ── Step 1: fromRealm + toRealm ──
    print(`    Backfilling fromRealm/toRealm on ${edgeCount} edges...`);
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
          fromRealm: { $ifNull: [{ $arrayElemAt: ["$_f.realm", 0] }, "Unknown"] },
          toRealm: { $ifNull: [{ $arrayElemAt: ["$_t.realm", 0] }, "Unknown"] },
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
    print(`    fromRealm/toRealm populated on ${realmCount} edges in ${Date.now() - t0}ms.`);

    // Distribution check
    const realmDist = edges.aggregate([
      { $group: { _id: { fromRealm: "$fromRealm", toRealm: "$toRealm" }, count: { $sum: 1 } } },
      { $sort: { count: -1 } },
    ]).toArray();
    print("    Realm distribution:");
    realmDist.forEach(r => print(`      ${r._id.fromRealm} -> ${r._id.toRealm}: ${r.count}`));

    // ── Step 2: reverseLabel ──
    const labelsCount = db.getCollection("kg.labels").countDocuments();
    let reverseLabelCount = 0;

    if (labelsCount === 0) {
      print("    WARNING: kg.labels is empty — reverseLabel will not be populated.");
      print("    Run migration 0004 first, then re-run this migration.");
    } else {
      print("    Backfilling reverseLabel from kg.labels...");
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
                vars: { rev: { $arrayElemAt: ["$_l.reverse", 0] } },
                in: {
                  $cond: [
                    { $and: [{ $ne: ["$$rev", null] }, { $ne: ["$$rev", ""] }] },
                    "$$rev",
                    "$$REMOVE",
                  ],
                },
              },
            },
          },
        },
        { $unset: "_l" },
        { $merge: { into: "kg.edges", on: "_id", whenMatched: "merge" } },
      ]).toArray();

      reverseLabelCount = edges.countDocuments({ reverseLabel: { $exists: true } });
      print(`    reverseLabel on ${reverseLabelCount}/${edgeCount} edges in ${Date.now() - t1}ms.`);
      print(`    (${edgeCount - reverseLabelCount} edges have no registered reverse — one-way labels)`);
    }

    return { realmCount, reverseLabelCount };
  },
};
