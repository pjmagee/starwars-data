// Migration 0018: Rewind the incremental-wiki-sync watermark to recover the gap
//
// The daily incremental sync (PageDownloader.IncrementalSyncAsync) formatted its
// MediaWiki `allrevisions` query timestamps with DateTime.ToString("o"), which
// emits 7 fractional-second digits. MediaWiki rejects that with a "badtimestamp"
// error returned as HTTP 200, so the call did not throw — the sync found
// "0 changed pages" every run yet still advanced its watermark
// (raw.job_state._id="IncrementalSync".updatedAt) to "now". Net effect: the job
// "ran" daily but synced nothing from 2026-03-18 (commit 1e5018557a) onward.
//
// The code fix (ToWikiTimestamp + ThrowIfApiError) stops the bleeding but cannot
// self-heal: the watermark was bumped forward every day, so a fixed build would
// only look back ~24h and the ~2-month backlog would stay missed forever. This
// migration rewinds the watermark to just before the bug shipped so the next
// incremental run backfills everything changed since then (downstream
// invalidation into kg.crawl_state + search.chunks cascades from there).
//
// Idempotent / environment-safe by construction:
//   - The runner tracks migrations per-DB, so it executes at most once anyway.
//   - The update is conditional ({ updatedAt: { $gt: BOUNDARY } }): it only ever
//     moves the watermark *backwards*. A fresh env with no IncrementalSync doc,
//     or one already older than the boundary, is left untouched (matchedCount 0)
//     — re-running can never push the watermark forward or lose progress.

globalThis.__currentMigration = {
  id: "0018-rewind-incremental-sync-watermark",
  description: "Rewind raw.job_state IncrementalSync watermark to 2026-03-18 to backfill the sync gap",

  up(db) {
    // Day the timestamp bug shipped (commit 1e5018557a). Re-fetching pages that
    // did not actually change is cheap — ProcessPageBatchAsync compares content
    // hashes, so only genuine changes trigger downstream re-processing.
    const BOUNDARY = new Date("2026-03-18T00:00:00Z");
    const jobState = db.getCollection("raw.job_state");

    const current = jobState.findOne({ _id: "IncrementalSync" });
    if (!current) {
      print("    No IncrementalSync watermark present — nothing to rewind (fresh environment).");
      return { rewound: false, reason: "no-watermark" };
    }

    print(`    Current watermark: ${current.updatedAt && current.updatedAt.toISOString()}`);

    const r = jobState.updateOne(
      { _id: "IncrementalSync", updatedAt: { $gt: BOUNDARY } },
      { $set: { updatedAt: BOUNDARY } }
    );

    if (r.matchedCount === 0) {
      print(`    Watermark already at/older than ${BOUNDARY.toISOString()} — left untouched.`);
      return { rewound: false, reason: "already-old", boundary: BOUNDARY };
    }

    print(`    Rewound watermark to ${BOUNDARY.toISOString()} — next incremental sync will backfill the gap.`);
    return { rewound: true, from: current.updatedAt, to: BOUNDARY };
  },
};
