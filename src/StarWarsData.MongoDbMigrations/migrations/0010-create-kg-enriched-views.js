// Migration 0010: Create the Phase 2 enriched read views
//
//   kg.nodes.enriched  — kg.nodes ⨝ kg.enrichments (active, hash-matched)
//   kg.edges.enriched  — kg.edges ⨝ kg.edge_enrichments (active, hash-matched)
//
// These views surface the merged "infobox + Holocron" picture to read consumers
// without modifying the base collections. Phase 1 owns kg.nodes / kg.edges and
// performs delete-and-reinsert; Holocron owns the enrichment collections and
// is append-only. The strict separation is enforced by the architecture in
// eng/design/018-kg-enrichments-architecture.md.
//
// Each view's $lookup filters enrichments by:
//   - status === "Active"  (superseded / stale / rejected hidden)
//   - contentHashAtCreation === <node's current contentHash>  (stale rows hidden)
//
// The merged shape exposes the original document as-is plus an `enrichments`
// array. The actual property/edge merge with provenance flags lives in C# read
// services (Stage E1+) — keeping the view's $lookup pure and the precedence
// logic explicit + testable in code.
//
// Idempotent: drops and recreates each view.
globalThis.__currentMigration = {
  id: "0010-create-kg-enriched-views",
  description: "Create kg.nodes.enriched + kg.edges.enriched read views (Design-018)",

  up(db) {
    // ── kg.nodes.enriched ───────────────────────────────────────────────
    try {
      db.getCollection("kg.nodes.enriched").drop();
      print("    Dropped existing kg.nodes.enriched.");
    } catch (_) {
      // View didn't exist
    }

    db.createView("kg.nodes.enriched", "kg.nodes", [
      {
        $lookup: {
          from: "kg.enrichments",
          let: { nodePageId: "$_id", nodeHash: "$contentHash" },
          pipeline: [
            {
              $match: {
                $expr: {
                  $and: [
                    { $eq: ["$pageId", "$$nodePageId"] },
                    { $eq: ["$status", "Active"] },
                    { $eq: ["$contentHashAtCreation", "$$nodeHash"] },
                  ],
                },
              },
            },
            { $sort: { createdAt: -1 } },
          ],
          as: "enrichments",
        },
      },
      {
        $addFields: {
          _enrichmentCount: { $size: "$enrichments" },
        },
      },
    ]);

    const nodesViewCount = db
      .getCollection("kg.nodes.enriched")
      .aggregate([{ $count: "n" }])
      .toArray();
    const nodesCount = nodesViewCount.length > 0 ? nodesViewCount[0].n : 0;

    print(`    Created kg.nodes.enriched: ${nodesCount} docs (matches kg.nodes count).`);

    // ── kg.edges.enriched ───────────────────────────────────────────────
    try {
      db.getCollection("kg.edges.enriched").drop();
      print("    Dropped existing kg.edges.enriched.");
    } catch (_) {
      // View didn't exist
    }

    db.createView("kg.edges.enriched", "kg.edges", [
      {
        $lookup: {
          from: "kg.edge_enrichments",
          let: {
            edgeFromId: "$fromId",
            edgeToId: "$toId",
            edgeLabel: "$label",
          },
          pipeline: [
            {
              $match: {
                $expr: {
                  $and: [
                    { $eq: ["$fromId", "$$edgeFromId"] },
                    { $eq: ["$toId", "$$edgeToId"] },
                    { $eq: ["$label", "$$edgeLabel"] },
                    { $eq: ["$status", "Active"] },
                  ],
                },
              },
            },
            { $sort: { createdAt: -1 } },
          ],
          as: "enrichments",
        },
      },
      {
        $addFields: {
          _enrichmentCount: { $size: "$enrichments" },
        },
      },
    ]);

    const edgesViewCount = db
      .getCollection("kg.edges.enriched")
      .aggregate([{ $count: "n" }])
      .toArray();
    const edgesCount = edgesViewCount.length > 0 ? edgesViewCount[0].n : 0;

    print(`    Created kg.edges.enriched: ${edgesCount} docs (matches kg.edges count).`);

    return {
      nodesEnrichedCount: nodesCount,
      edgesEnrichedCount: edgesCount,
    };
  },
};
