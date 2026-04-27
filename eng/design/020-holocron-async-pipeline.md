# 020 — Async Holocron pipeline (full-corpus enhancement, change-aware re-runs)

Status: in progress
Date: 2026-04-27
Author: Patrick Magee
Cross-refs: [Design-018 — KG enrichments architecture](018-kg-enrichments-architecture.md), [Design-019 — KG enrichment UI provenance](019-kg-enrichment-ui-provenance.md)

## Problem

The v1 Holocron agent runs as a single synchronous LLM call per node, with the
context window forcibly capped at ~5 backlink chunks. For high-degree entities
(Sidious has 1,686 articles linking to him; Vader, Anakin, Luke similarly
saturated), capping at 5 is sampling 0.3% of available evidence. The user's
requirement: **process all available content** so the agent can build a complete
enhanced node. Sampling defeats the goal of "building an enhanced KG".

The Timeline pipeline already proved the pattern for *long-form* per-node
enrichment: Discovery → Bundler → BatchExtractor → Consolidator → Review with
checkpointed progress. This design ports that pattern to Holocron and adds
two needs the Timeline pipeline didn't have:

1. **Async job orchestration** — interactive button kicks off, job runs in the
   background, UI polls. 1,686 chunks at ~5s/batch ≈ 15 min sync wall-time.
2. **Change-aware re-runs** — when a node is enhanced again, skip chunks whose
   content hasn't changed since the last successful job. The system should be
   smart about not redoing work on stale-vs-fresh content.

## Goals

1. **Process the full backlink corpus** for any node. No 5-chunk cap.
2. **Async kickoff** — POST returns `jobId` immediately; Hangfire runs the
   pipeline; UI polls `GET /api/holocron/jobs/{id}` for progress.
3. **Change-aware** — second run on the same node is fast and free if no
   linking article has changed. Detection via `chunk.contentHash` compared
   against the per-`(nodeId, chunkId)` processing log.
4. **Resumable** — pipeline state checkpointed in MongoDB so a process restart
   mid-job picks up where it left off.
5. **Visible** — a `/holocron/jobs` page lists pending / in-progress /
   completed jobs with progress bar, last-processed timestamp per node, and a
   summary of enrichments produced.

## Non-goals

- **Real-time streaming** of partial results during a run. Final apply is one
  atomic merge; intermediate proposals stay in the job doc.
- **Multi-tenancy** of jobs. One job per node at a time — duplicate enqueue is
  a no-op while one is `Queued`/`Running`.
- **Rolling back** completed jobs. Use the existing per-enrichment `Reject`
  workflow if a job's output is bad.

## Architecture

### Collections

#### `kg.enrichment_jobs` (new)
Records each enhance request and its lifecycle.

```
{
  _id: ObjectId,
  pageId: 452582,                  // node being enhanced
  nodeName: "Darth Sidious",       // snapshot for UI
  status: "Queued|Discovering|Bundling|Extracting|Consolidating|Applying|Completed|Failed",
  triggeredBy: "manual|scheduled|admin",
  agentVersion: "holocron-v2.0.0",
  modelId: "gpt-5.4",
  createdAt: ISODate,
  startedAt: ISODate?,
  completedAt: ISODate?,
  // Discovery / Bundler stats
  chunksDiscovered: 1686,
  chunksProcessedNew: 1300,        // changed or first-time-seen
  chunksSkippedUnchanged: 386,     // identical content already processed
  totalBatches: 130,
  // Extractor progress (live during run)
  completedBatches: 47,
  // Consolidator / Apply summary (final)
  proposalsExtracted: 412,
  enrichmentsApplied: 38,
  duplicatesDropped: 287,
  evidenceFailures: 12,
  preflightRejects: 75,
  // Final state
  error: "..."?,
}
```

Indexes: `{pageId: 1, status: 1}`, `{status: 1, createdAt: -1}` for the jobs page.

#### `kg.node_processed_chunks` (new)
Per-(node, chunk) processing log. The change-detection ledger.

```
{
  _id: ObjectId,
  nodeId: 452582,                  // the node being enhanced
  chunkId: ObjectId,               // search.chunks._id
  chunkContentHash: "sha256:...",  // content hash at processing time
  processedAt: ISODate,
  jobId: ObjectId,
  agentVersion: "holocron-v2.0.0",
}
```

Indexes: `{nodeId: 1, chunkId: 1}` unique. `{nodeId: 1}` for "all chunks
processed for this node".

Why a separate collection (not embedded on the chunk or the node): chunks are
shared across many enrichment subjects; embedding the log on the chunk would
explode `chunk` doc size. Embedding on the node would have the same problem
in reverse. A join-collection is right.

#### `search.chunks` schema addition
Add `contentHash: string` field. SHA256 of `text` computed at chunk-write
time. The chunker re-creates chunks (existing behaviour: skip pages already
chunked, full re-process when forced), so the hash gives stable identity
across re-chunkings even if `_id` changes. Backfilled by migration 0013.

### The pipeline (5 executors, mirrors Timeline)

```
POST /api/holocron/jobs/enhance/{nodeId}
  → enqueues HolocronEnhanceJob via Hangfire (one job per nodeId)
  → returns { jobId, status: "Queued" }

[Hangfire worker]
  ↓
HolocronContextDiscoveryExecutor      [pure C#, no LLM]
  - Pull node from kg.nodes
  - Find all chunks where links contains node.wikiUrl AND pageId != node.pageId
  - For each chunk, look up (nodeId, chunkId) in kg.node_processed_chunks
  - Skip chunks where contentHash matches the processed record (unchanged)
  - Persist remaining "new chunks" set in workflow state
  - Update job doc: chunksDiscovered, chunksSkippedUnchanged, chunksProcessedNew
  ↓
HolocronBundlerExecutor               [pure C#, no LLM]
  - Group chunks into ~40K-char batches (matches Timeline's budget)
  - Each batch carries: target node info + canonical labels + this batch's chunks
  - Persist batches in workflow state, update job.totalBatches
  ↓
HolocronProposalExtractorExecutor     [LLM, one call per batch]
  - For each batch (resumable via in-memory checkpoint mirroring Timeline's
    BatchExtractionExecutor pattern):
      - Call LLM with the four-array structured-output schema (existing)
      - Append all proposals to accumulated state
      - Update job.completedBatches
  - On restart, skip batches whose index is in the checkpointed processed set
  ↓
HolocronConsolidatorExecutor          [pure C#, no LLM]
  - Annotate proposals: dedupe by (fromId, toId, label), prefer the one with
    most evidence + longest description
  - Add proposals: dedupe by unordered NodePairKey, first-evidence-quality wins
  - Augment proposals: dedupe by (fieldPath, value)
  - FillGap: dedupe by (fromId, toId, label, fieldName)
  - Apply pre-flight rules (canonical labels, no parallel pairs, evidence
    must reference real chunks/pages) — same rules as v1
  - Update job.proposalsExtracted, duplicatesDropped, preflightRejects
  ↓
HolocronApplyExecutor                 [pure C#, no LLM]
  - Insert all surviving enrichments to kg.enrichments / kg.edge_enrichments
  - Insert HolocronEvent entries to kg.events
  - Insert ProcessedChunk records for every chunk this job consumed
    (so the next run knows what's already been done)
  - Update job.enrichmentsApplied, status=Completed, completedAt
```

### Job lifecycle states

```
Queued → Discovering → Bundling → Extracting → Consolidating → Applying → Completed
                                       ↑
                                   resumable via checkpoint
                                       
                                   ↓ (any stage)
                                 Failed (with error)
```

### Skip-if-unchanged flow

When the user re-runs Enhance on a node:

1. Discovery finds all 1,686 backlink chunks again.
2. For each chunk, look up `kg.node_processed_chunks` where
   `nodeId == thisNode AND chunkId == thisChunk`.
3. If a record exists AND its `chunkContentHash` matches the chunk's current
   `contentHash` → skip. Bump `chunksSkippedUnchanged`.
4. Otherwise → include in the next batch. Bump `chunksProcessedNew`.

If the user re-runs on Sidious tomorrow and no linking article has changed,
discovery will skip all 1,686, the bundler creates 0 batches, the extractor
runs 0 LLM calls, and the apply step is a no-op. **Cost: zero.** The job
completes in seconds with `enrichmentsApplied: 0` and a clear breakdown.

### Async kickoff

```
POST /api/holocron/jobs/enhance/{pageId}
  → if a Queued/Running job already exists for this pageId, return that one
  → else: create job doc with status=Queued, enqueue via Hangfire
  → respond 202 Accepted with { jobId, status }

GET /api/holocron/jobs/{jobId}
  → returns the full job doc

GET /api/holocron/jobs?status=Running&nodeId=...
  → paginated list with filters

GET /api/holocron/jobs/last-processed/{pageId}
  → returns the most recent Completed job for this node, or null
```

### UI: `/holocron/jobs`

Mirrors the Holocron Log layout:
- Filter row: status, node search (autocomplete using shared
  `EntitySearchDto`), node type
- Table: status chip, node name, started time, progress bar
  (`completedBatches / totalBatches`), enrichments count, duration, action
  (cancel / view details)
- Auto-poll every 5s while any Running/Queued jobs are visible

The existing "Enhance with Holocron" button changes from synchronous-call to
async-kickoff: returns immediately, shows toast "Job queued — track at
/holocron/jobs". A small "Last processed: 2 hours ago" caption appears under
the button when a Completed job exists for that node.

## Implementation phases

| Phase | Scope | LOC est | Status |
|---|---|---|---|
| A | `kg.enrichment_jobs` + `kg.node_processed_chunks` schemas, `chunk.contentHash` field + extractor + migration 0013 backfill, `HolocronJobService` (create/update/get/list) | ~400 | Pending |
| B | 5 executors + workflow wiring + Hangfire kickoff + skip-if-unchanged logic | ~1200 | Pending |
| C | `/holocron/jobs` page + last-processed caption on Enhance button | ~300 | Pending |

Phase A unblocks the rest. Each phase ships as one commit on the branch.

## Risks

1. **Cost runaway.** A high-degree node like Sidious produces ~170 batches at
   ~$0.01/batch ≈ $1.70 per enhance. Mitigation: per-job `maxBatches` cap
   (configurable, default unlimited but warns above 200), and per-day budget
   check before kicking off scheduled passes. Document this in the daily-pass
   Hangfire job.
2. **Mid-job page edits.** A linking article gets re-chunked while the job is
   running → `chunkId` changes mid-flight. Mitigation: Discovery snapshots the
   chunk set in workflow state at the start; later stages don't re-query.
   Re-run the job to pick up the change.
3. **Consolidator drops valuable variants.** Two Annotate proposals for the
   same `(fromId, toId, label)` with subtly different `role` strings. The
   "longest description wins" heuristic might lose the better one. Mitigation:
   the agent's per-batch context already overlaps significantly, so most
   duplicates are *near-duplicates* with the same intent. If this becomes a
   visible problem, add an LLM-based merge step (small extra call to pick
   between two competing claims).
4. **Hangfire dashboard already exists** — make sure the new jobs don't
   pollute it. Use a dedicated queue (`holocron`) so the existing Hangfire
   stats stay clean.

## Open questions

1. Should the job page be public or admin-only? (Holocron Log is public; jobs
   leak more operational detail.) **Recommendation:** public, read-only —
   matches the Log's transparency posture.
2. What's the daily-pass behaviour? Pick N nodes, kick off N jobs, let them
   run sequentially in the dedicated Hangfire queue? **Recommendation:** yes,
   serial — avoids concurrent OpenAI quota spikes.
3. Cancellation? **Recommendation:** v1 ships without explicit cancel; jobs
   are cheap to fail mid-flight and the resumable checkpoint means a stuck
   one can be deleted from the Hangfire dashboard. Add Cancel button in v2 if
   the failure mode appears.

## Verification

- Phase A: schemas exist, migration runs cleanly, sample chunk has
  `contentHash` populated.
- Phase B: enhance Sidious end-to-end. First run creates a job, processes all
  ~170 batches over ~15 min, lands hundreds of enrichments. Second run
  immediately after creates a job that completes in <30s with
  `chunksSkippedUnchanged == chunksDiscovered`.
- Phase C: `/holocron/jobs` shows the live job with progress; on completion,
  the Enhance button on the Knowledge Graph page shows "Last processed: just
  now".
