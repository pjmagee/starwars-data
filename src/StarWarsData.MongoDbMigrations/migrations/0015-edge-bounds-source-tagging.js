// Migration 0015: Retroactive edge-bounds provenance tagging (Design-021)
//
// Tags every existing kg.edges row with `meta.boundsSource` so the Holocron
// agent and the read-side merge layers can distinguish hard infobox-supplied
// bounds from soft lifecycle-fallback derivations.
//
// The Phase 5 ETL pipeline (InfoboxGraphService) writes new edges with the
// correct tag from now on. This migration covers the existing corpus —
// re-derive what Phase 5's lifecycle-fallback WOULD have produced for each
// edge, compare to the stored bounds, and tag accordingly:
//
//   • bounds match the lifecycle-derivation     → "Lifecycle"
//   • bounds present but don't match derivation → "Infobox"
//   • both bounds null                          → leave untagged (Unknown)
//
// The lifecycle-derivation matches the C# logic in InfoboxGraphService.cs:
//
//   if both endpoints have startYear:
//     from = max(srcStart, tgtStart)
//     to   = min(srcEnd, tgtEnd) when both have endYear, else whichever is set, else null
//     commit only if to is null OR from <= to
//   elif only src has startYear: from = srcStart, to = srcEnd
//   elif only tgt has startYear: from = tgtStart, to = tgtEnd
//
// Idempotent — re-running the migration tags edges to the same value. Edges
// added after the first run (by ETL or by Holocron Add) already carry the
// tag from C# and pass through unchanged.
//
// Counts ~700K edges across kg.edges — runs in a single pass with batched
// bulkWrite (5K edges per batch) to keep the working set bounded.

globalThis.__currentMigration = {
  id: "0015-edge-bounds-source-tagging",
  description:
    "Retroactively tag kg.edges.meta.boundsSource as Lifecycle / Infobox / Unknown (Design-021)",

  up(db) {
    print("    Loading kg.nodes lifecycle map...");
    const nodeMap = new Map();
    let nodeCount = 0;
    const nodeCursor = db
      .getCollection("kg.nodes")
      .find({}, { _id: 1, startYear: 1, endYear: 1 });
    nodeCursor.forEach((n) => {
      nodeMap.set(n._id, {
        start: typeof n.startYear === "number" ? n.startYear : null,
        end: typeof n.endYear === "number" ? n.endYear : null,
      });
      nodeCount++;
    });
    print(`    Loaded ${nodeCount} node lifecycles.`);

    /**
     * Re-derive what Phase 5's lifecycle-fallback would have produced for
     * the (fromId, toId) pair. Returns {from, to} both numeric-or-null,
     * or null when no derivation is possible.
     */
    function deriveLifecycle(fromId, toId) {
      const src = nodeMap.get(fromId);
      const tgt = nodeMap.get(toId);
      const hasSrcStart = src && src.start !== null;
      const hasTgtStart = tgt && tgt.start !== null;

      if (hasSrcStart && hasTgtStart) {
        const from = Math.max(src.start, tgt.start);
        let to = null;
        if (src.end !== null && tgt.end !== null) {
          to = Math.min(src.end, tgt.end);
        } else if (src.end !== null) {
          to = src.end;
        } else if (tgt.end !== null) {
          to = tgt.end;
        }
        // Phase 5 only commits when "to is null OR from <= to" — guard against
        // intersections where the intervals don't overlap.
        if (to !== null && from > to) return null;
        return { from, to };
      }
      if (hasSrcStart && !hasTgtStart) {
        return { from: src.start, to: src.end };
      }
      if (hasTgtStart && !hasSrcStart) {
        return { from: tgt.start, to: tgt.end };
      }
      return null;
    }

    function bound(v) {
      return typeof v === "number" ? v : null;
    }

    print("    Scanning kg.edges and tagging boundsSource...");
    const edgeCursor = db
      .getCollection("kg.edges")
      .find({}, { _id: 1, fromId: 1, toId: 1, fromYear: 1, toYear: 1, meta: 1 });

    let tallied = 0;
    let lifecycleTag = 0;
    let infoboxTag = 0;
    let unknownSkip = 0;
    let alreadyTagged = 0;
    const ops = [];
    const BATCH = 5000;

    function flush() {
      if (ops.length === 0) return;
      db.getCollection("kg.edges").bulkWrite(ops, { ordered: false });
      ops.length = 0;
    }

    edgeCursor.forEach((e) => {
      tallied++;
      // If a tag already exists, skip — migration is idempotent and re-runs
      // shouldn't churn the same docs.
      if (e.meta && typeof e.meta.boundsSource === "string") {
        alreadyTagged++;
        return;
      }

      const fromYear = bound(e.fromYear);
      const toYear = bound(e.toYear);

      // No bounds at all — leave untagged (Unknown is the default semantics).
      if (fromYear === null && toYear === null) {
        unknownSkip++;
        return;
      }

      const lifecycle = deriveLifecycle(e.fromId, e.toId);
      const matchesLifecycle =
        lifecycle !== null &&
        lifecycle.from === fromYear &&
        lifecycle.to === toYear;
      const tag = matchesLifecycle ? "Lifecycle" : "Infobox";
      if (matchesLifecycle) lifecycleTag++;
      else infoboxTag++;

      ops.push({
        updateOne: {
          filter: { _id: e._id },
          update: { $set: { "meta.boundsSource": tag } },
        },
      });
      if (ops.length >= BATCH) flush();
    });
    flush();

    const result = {
      edgesScanned: tallied,
      lifecycleTagged: lifecycleTag,
      infoboxTagged: infoboxTag,
      bothBoundsNullSkipped: unknownSkip,
      alreadyTaggedSkipped: alreadyTagged,
    };
    print(
      `    Done: scanned=${tallied}, Lifecycle=${lifecycleTag}, Infobox=${infoboxTag}, both-null=${unknownSkip}, already-tagged=${alreadyTagged}`,
    );
    return result;
  },
};
