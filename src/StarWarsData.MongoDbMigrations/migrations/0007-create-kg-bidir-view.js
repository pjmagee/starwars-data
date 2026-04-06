// Migration 0007: Create the kg.edges.bidir bidirectional view
//
// Exposes each edge twice: once as-stored (forward) and once flipped with
// label replaced by reverseLabel (reverse). See Design-007.
//
// Prerequisites: kg.edges must have reverseLabel populated (0005).
// Idempotent: drops and recreates the view.
globalThis.__currentMigration = {
  id: "0007-create-kg-bidir-view",
  description: "Create kg.edges.bidir bidirectional view (Design-007)",

  up(db) {
    const edges = db.getCollection("kg.edges");
    const totalCount = edges.countDocuments();
    const revCount = edges.countDocuments({ reverseLabel: { $exists: true } });

    print(`    kg.edges: ${revCount}/${totalCount} edges have reverseLabel (${((revCount / totalCount) * 100).toFixed(1)}%)`);

    if (revCount === 0) {
      throw new Error("No edges have reverseLabel populated. Run migration 0005 first.");
    }

    // Drop existing view if present
    try {
      db.getCollection("kg.edges.bidir").drop();
      print("    Dropped existing kg.edges.bidir.");
    } catch (_) {
      // View didn't exist
    }

    // Create view matching InfoboxGraphService.EnsureBidirectionalEdgesViewAsync
    db.createView("kg.edges.bidir", "kg.edges", [
      // Forward branch: every edge as-stored
      { $addFields: { direction: "forward" } },
      // Reverse branch: edges with reverseLabel, endpoints flipped
      {
        $unionWith: {
          coll: "kg.edges",
          pipeline: [
            { $match: { reverseLabel: { $exists: true, $nin: [null, ""] } } },
            {
              $project: {
                _id: 1,
                fromId: "$toId",
                toId: "$fromId",
                fromName: "$toName",
                toName: "$fromName",
                fromType: "$toType",
                toType: "$fromType",
                fromRealm: "$toRealm",
                toRealm: "$fromRealm",
                label: "$reverseLabel",
                weight: 1,
                evidence: 1,
                continuity: 1,
                fromYear: 1,
                toYear: 1,
                sourcePageId: 1,
                direction: { $literal: "reverse" },
              },
            },
          ],
        },
      },
    ]);

    // Verify
    const viewCount = db.getCollection("kg.edges.bidir")
      .aggregate([{ $count: "n" }])
      .toArray()[0].n;

    print(`    Created kg.edges.bidir: ${viewCount} docs (${totalCount} forward + ${viewCount - totalCount} reverse).`);

    return { totalCount, forwardCount: totalCount, reverseCount: viewCount - totalCount };
  },
};
