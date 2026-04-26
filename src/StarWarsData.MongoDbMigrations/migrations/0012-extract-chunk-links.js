// Migration 0012: Extract Wookieepedia <a href> URLs into search.chunks.links
//
// Problem: The Holocron agent wants to use "what links here" as a context source —
// for any node being enhanced, find chunks of OTHER articles whose prose links
// to this node's wiki page. The chunk corpus already stores raw HTML in `text`
// (so the URLs are present), but searching the URL with an unindexed regex over
// 800K+ chunks took >240s — unusable.
//
// Fix: extract every `<a href="https://starwars.fandom.com/wiki/...">` URL out of
// each chunk's text once and store the deduped set on a `links: string[]` field,
// then add a multikey index. Equality lookup on the multikey index is O(log N)
// — fast enough for the agent to use as a primary context source.
//
// The chunking pipeline (ArticleChunkingService.ExtractWikiLinks) populates this
// field for new writes; this migration backfills the existing corpus.
//
// Idempotent: re-running just recomputes `links` from `text` (same value if text
// hasn't changed) and ensureIndex returns ok if the index already exists.

globalThis.__currentMigration = {
  id: "0012-extract-chunk-links",
  description: "Extract <a href> URLs into search.chunks.links + multikey index",

  up(db) {
    const chunks = db.getCollection("search.chunks");
    const total = chunks.countDocuments({});
    print(`    Backfilling links on ${total} chunks via aggregation pipeline...`);

    // updateMany with an aggregation pipeline runs server-side — no per-doc round-trip.
    // $regexFindAll returns one match doc per occurrence, so we $map to grab the first
    // capture group (the URL itself) and $setUnion to dedupe within a chunk.
    const start = Date.now();
    const result = chunks.updateMany(
      {},
      [
        {
          $set: {
            links: {
              $setUnion: [
                {
                  $map: {
                    input: {
                      $regexFindAll: {
                        input: "$text",
                        regex: /href="(https:\/\/starwars\.fandom\.com\/wiki\/[^"]+)"/,
                      },
                    },
                    as: "m",
                    in: { $arrayElemAt: ["$$m.captures", 0] },
                  },
                },
              ],
            },
          },
        },
      ],
    );
    const elapsedSec = ((Date.now() - start) / 1000).toFixed(1);
    print(`    Updated ${result.modifiedCount} / ${result.matchedCount} chunks in ${elapsedSec}s.`);

    // Multikey index — one btree entry per element of the links array. The Holocron
    // agent's BuildContextAsync uses { links: targetWikiUrl } equality lookups, which
    // hit this index directly.
    print("    Creating multikey index { links: 1 }...");
    const indexName = chunks.createIndex({ links: 1 }, { name: "links_1" });
    print(`    Index '${indexName}' ready.`);

    // Quick sanity check: pick one chunk that links to Darth Sidious and confirm.
    const sample = chunks.findOne({ links: "https://starwars.fandom.com/wiki/Darth_Sidious" }, { _id: 1, pageId: 1, title: 1 });
    if (sample) {
      print(`    Sample backlink: chunk ${sample._id} (page ${sample.pageId}, title "${sample.title}") links to Darth Sidious.`);
    } else {
      print("    Warning: no chunk found linking to Darth Sidious. Either the corpus is empty or the regex didn't match — investigate before relying on the index.");
    }

    return {
      modifiedCount: result.modifiedCount,
      indexName,
      sampleFound: !!sample,
    };
  },
};
