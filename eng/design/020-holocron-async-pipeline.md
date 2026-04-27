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

---

## Phase A — Implementation specification

This section captures the concrete file-level changes required for Phase A.
Written so a fresh context (or another agent) can pick this up after a
compaction without losing fidelity.

### File-level changes

| Path | Change |
|---|---|
| `src/StarWarsData.Models/KnowledgeGraph/Holocron/HolocronJob.cs` | NEW — `HolocronJob` POCO (collection schema) + `HolocronJobStatus` enum |
| `src/StarWarsData.Models/KnowledgeGraph/Holocron/ProcessedChunk.cs` | NEW — `ProcessedChunk` POCO for the processing ledger |
| `src/StarWarsData.Models/Search/ArticleChunk.cs` | ADD `ContentHash` property (`[BsonElement("contentHash")]`) |
| `src/StarWarsData.Models/Settings.cs` | ADD `Collections.KgEnrichmentJobs = "kg.enrichment_jobs"` and `Collections.KgNodeProcessedChunks = "kg.node_processed_chunks"` |
| `src/StarWarsData.Services/AI/Agents/HolocronJobService.cs` | NEW — CRUD + lifecycle helpers for jobs |
| `src/StarWarsData.Services/Search/ArticleChunkingService.cs` | ADD `ComputeContentHash(string text)` static helper + populate `ContentHash` on the new chunk row |
| `src/StarWarsData.MongoDbMigrations/migrations/0013-chunk-content-hash.js` | NEW — backfill `contentHash` via cursor + bulkWrite, plus index on `contentHash` |
| `src/StarWarsData.ApiService/Program.cs` | Register `HolocronJobService` in DI (singleton; just wraps Mongo collections) |

### Models

`HolocronJob`:
```csharp
public sealed class HolocronJob
{
    [BsonId] [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("pageId")]
    public int PageId { get; set; }

    [BsonElement("nodeName")]
    public string NodeName { get; set; } = string.Empty;

    [BsonElement("status")] [BsonRepresentation(BsonType.String)]
    public HolocronJobStatus Status { get; set; } = HolocronJobStatus.Queued;

    [BsonElement("triggeredBy")]
    public string TriggeredBy { get; set; } = "manual"; // "manual" | "scheduled" | "admin"

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = HolocronAgent.AgentVersion;

    [BsonElement("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("startedAt")] [BsonIgnoreIfNull]
    public DateTime? StartedAt { get; set; }

    [BsonElement("completedAt")] [BsonIgnoreIfNull]
    public DateTime? CompletedAt { get; set; }

    // Discovery / Bundler stats
    [BsonElement("chunksDiscovered")] public int ChunksDiscovered { get; set; }
    [BsonElement("chunksProcessedNew")] public int ChunksProcessedNew { get; set; }
    [BsonElement("chunksSkippedUnchanged")] public int ChunksSkippedUnchanged { get; set; }
    [BsonElement("totalBatches")] public int TotalBatches { get; set; }
    [BsonElement("completedBatches")] public int CompletedBatches { get; set; }

    // Final apply summary
    [BsonElement("proposalsExtracted")] public int ProposalsExtracted { get; set; }
    [BsonElement("enrichmentsApplied")] public int EnrichmentsApplied { get; set; }
    [BsonElement("duplicatesDropped")] public int DuplicatesDropped { get; set; }
    [BsonElement("evidenceFailures")] public int EvidenceFailures { get; set; }
    [BsonElement("preflightRejects")] public int PreflightRejects { get; set; }

    [BsonElement("error")] [BsonIgnoreIfNull]
    public string? Error { get; set; }
}

public enum HolocronJobStatus
{
    Queued, Discovering, Bundling, Extracting, Consolidating, Applying, Completed, Failed
}
```

`ProcessedChunk`:
```csharp
public sealed class ProcessedChunk
{
    [BsonId] [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("nodeId")]
    public int NodeId { get; set; }

    [BsonElement("chunkId")] [BsonRepresentation(BsonType.ObjectId)]
    public string ChunkId { get; set; } = string.Empty;

    [BsonElement("chunkContentHash")]
    public string ChunkContentHash { get; set; } = string.Empty;

    [BsonElement("processedAt")]
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("jobId")] [BsonRepresentation(BsonType.ObjectId)]
    public string JobId { get; set; } = string.Empty;

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;
}
```

### `HolocronJobService` API

```csharp
public sealed class HolocronJobService(IMongoClient client, IOptions<SettingsOptions> settings)
{
    private readonly IMongoCollection<HolocronJob> _jobs = ...; // kg.enrichment_jobs
    private readonly IMongoCollection<ProcessedChunk> _processed = ...; // kg.node_processed_chunks

    // Returns the existing Queued/Running job for this pageId, or creates a new one.
    // Atomic via findAndUpdate to avoid duplicate enqueue races.
    public Task<HolocronJob> EnqueueOrGetActiveAsync(int pageId, string nodeName, string triggeredBy, string modelId, CancellationToken ct);

    public Task<HolocronJob?> GetAsync(string jobId, CancellationToken ct);

    // Filters: status (multi), pageId, since, page, pageSize. Sort by createdAt desc.
    public Task<HolocronJobsPage> ListAsync(HolocronJobQuery query, CancellationToken ct);

    public Task<HolocronJob?> GetLastCompletedForNodeAsync(int pageId, CancellationToken ct);

    public Task TransitionAsync(string jobId, HolocronJobStatus to, CancellationToken ct);

    // Update specific fields atomically without overwriting others — used by each executor.
    public Task UpdateProgressAsync(string jobId, Action<UpdateDefinitionBuilder<HolocronJob>, List<UpdateDefinition<HolocronJob>>> apply, CancellationToken ct);

    public Task FailAsync(string jobId, string error, CancellationToken ct);

    // Processing ledger (used by Discovery and Apply).
    public Task<HashSet<(string ChunkId, string Hash)>> GetProcessedSetAsync(int nodeId, CancellationToken ct);
    public Task RecordProcessedAsync(int nodeId, IEnumerable<ProcessedChunk> records, CancellationToken ct);
}
```

`HolocronJobQuery` and `HolocronJobsPage` are simple records colocated in the same file.

### Chunk content hash

`ArticleChunkingService.ComputeContentHash(string text)`:
```csharp
public static string ComputeContentHash(string text)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty));
    return Convert.ToHexString(bytes); // lowercase preferred — use ToHexStringLower in .NET 9+
}
```

Populated at chunk-write time:
```csharp
new ArticleChunk { ..., ContentHash = ComputeContentHash(chunks[i].text) }
```

### Migration 0013

```javascript
// 0013-chunk-content-hash.js
// Backfills search.chunks.contentHash with sha256(text) and adds an index.
// Idempotent: re-running rehashes (same text → same hash) and createIndex
// returns ok if already present.
const crypto = require("crypto");

globalThis.__currentMigration = {
  id: "0013-chunk-content-hash",
  description: "Backfill search.chunks.contentHash + index { contentHash: 1 }",

  up(db) {
    const chunks = db.getCollection("search.chunks");
    const total = chunks.countDocuments({});
    print(`    Backfilling contentHash on ${total} chunks…`);

    const start = Date.now();
    let processed = 0;
    let bulk = [];
    chunks.find({}, { _id: 1, text: 1 }).forEach((doc) => {
      const hash = crypto.createHash("sha256").update(doc.text || "").digest("hex");
      bulk.push({ updateOne: { filter: { _id: doc._id }, update: { $set: { contentHash: hash } } } });
      if (bulk.length >= 1000) {
        chunks.bulkWrite(bulk);
        processed += bulk.length;
        bulk = [];
      }
    });
    if (bulk.length) {
      chunks.bulkWrite(bulk);
      processed += bulk.length;
    }
    const elapsed = ((Date.now() - start) / 1000).toFixed(1);
    print(`    Updated ${processed} chunks in ${elapsed}s.`);

    print("    Creating index { contentHash: 1 }…");
    const indexName = chunks.createIndex({ contentHash: 1 }, { name: "contentHash_1" });
    print(`    Index '${indexName}' ready.`);

    return { processed, indexName };
  },
};
```

Apply via `mongosh` against `starwars-dev` once Phase A code is merged. Same
pattern as 0012 — no Aspire migration container restart needed for dev work,
but the file ships in the migrations folder so prod environments pick it up.

### Indexes to create

Done in code-side (`HolocronJobService` constructor or a one-shot in DI
startup) since these collections are NEW and don't need a backfill migration:

```csharp
_jobs.Indexes.CreateMany([
    new(Builders<HolocronJob>.IndexKeys.Ascending(j => j.PageId).Ascending(j => j.Status)),
    new(Builders<HolocronJob>.IndexKeys.Ascending(j => j.Status).Descending(j => j.CreatedAt)),
]);

_processed.Indexes.CreateMany([
    new(Builders<ProcessedChunk>.IndexKeys.Ascending(p => p.NodeId).Ascending(p => p.ChunkId),
        new CreateIndexOptions { Unique = true, Name = "node_chunk_unique" }),
    new(Builders<ProcessedChunk>.IndexKeys.Ascending(p => p.NodeId)),
]);
```

### Phase A acceptance criteria

1. New collections exist with the indexes listed above.
2. `search.chunks.contentHash` populated for all 817K existing chunks via
   migration 0013.
3. `ArticleChunkingService` populates `contentHash` on new chunks going
   forward.
4. `HolocronJobService` resolves from DI in the API process.
5. Solution builds clean. No code path actually CONSUMES the new schemas yet —
   that's Phase B.

### What's deferred to Phase B

- Actually creating jobs (no endpoint yet).
- Hangfire wiring of the worker (`HolocronEnhanceJob.RunAsync`).
- Discovery executor that consults `kg.node_processed_chunks` to skip
  unchanged chunks.
- The 4 other executors.

### Phase B preview (concretely, for handoff continuity)

| File | Purpose |
|---|---|
| `src/StarWarsData.Services/AI/Agents/Holocron/HolocronEnhanceJob.cs` | Hangfire job entry-point; orchestrates the 5 executors |
| `src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronContextDiscoveryExecutor.cs` | Pure C# — backlink fetch + skip-if-unchanged filter |
| `src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronBundlerExecutor.cs` | Pure C# — bundle into ~40K-char batches |
| `src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronProposalExtractorExecutor.cs` | LLM per-batch — reuses `HolocronProposalsBatch` schema; checkpoints to `kg.enrichment_jobs.completedBatches` |
| `src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronConsolidatorExecutor.cs` | Pure C# — dedupe + pre-flight |
| `src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronApplyExecutor.cs` | Pure C# — write enrichments + events + ProcessedChunk records |
| `src/StarWarsData.ApiService/Features/Holocron/HolocronJobsController.cs` | NEW — `POST /jobs/enhance/{pageId}`, `GET /jobs/{id}`, `GET /jobs?status=...` |
| `src/StarWarsData.AppHost/Program.cs` | Register `holocron` Hangfire queue |

Phase B keeps the existing synchronous `POST /api/holocron/enhance/{pageId}`
endpoint as a thin wrapper that immediately enqueues a job and returns the
jobId — backwards-compatible with the Knowledge Graph "Enhance" button
(which already polls). The button copy gets updated in Phase C.

### Phase C preview

- `src/StarWarsData.Frontend/Components/Pages/HolocronJobs.razor` — new page
  at `/holocron/jobs` mirroring the Holocron Log layout (filter row +
  paginated table). Polls every 5s while any non-terminal jobs are visible.
- Knowledge Graph + Graph Explorer "Enhance with Holocron" buttons gain a
  caption underneath: "Last processed: 2 hours ago" pulled from
  `GET /api/holocron/jobs/last-completed/{pageId}`.
- Optional: live progress bar on the row in the Knowledge Graph node-detail
  panel while a job is running for that node.

## Handoff notes

If a fresh context picks up after a compaction:

1. Read [project_holocron_handoff.md](../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/project_holocron_handoff.md) for branch state.
2. Read [project_holocron_async_pipeline.md](../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/project_holocron_async_pipeline.md) for the design-020 summary.
3. This file (Design-020) is the canonical spec — schema, executor responsibilities, file paths, model definitions are all here.
4. The branch is `feature/holocron-skeleton`. As of the design-020 commit it
   carries: schema-driven proposal arrays, Annotate operation, Stage E1 UI,
   edges drill-down + virtualised table, Phase 2 attributes-as-table,
   name-or-Titles search, wiki-href backlink source (Source 2 already uses
   the indexed `Links` multikey field). Migration 0012 is applied to
   `starwars-dev` but **not yet to prod** (`starwars`).
5. Phase A will land as a single commit. Phase B as ~3-5 commits (one per
   executor + the controller). Phase C as one commit.

