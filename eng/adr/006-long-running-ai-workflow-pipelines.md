# ADR-006: Long-Running AI Workflow Pipelines on Microsoft.Agents.AI.Workflows

**Status:** Accepted
**Date:** 2026-04-27
**Decision maker:** Patrick Magee

## Context

Some AI work in this codebase is conversational and short-lived (the Ask AI agent, ADR-002). Other AI work is the opposite shape:

- A **single user-initiated request** that runs for **minutes to hours** without further interaction.
- Multiple **dependent stages** (discover → bundle → extract proposals → consolidate → apply) where later stages need outputs of earlier stages but are otherwise loosely coupled.
- **Per-iteration LLM batches** that may individually fail without invalidating the whole run.
- **Durability requirements** — the user closes the browser, opens it again ten minutes later, expects to see live progress, and if the process restarts mid-run we want to resume rather than lose work.
- **Idempotency requirements** — re-running the same job against unchanged inputs must be safe and (ideally) fast.

Two systems already fit this shape: **CharacterTimelines** (per-character LLM timeline construction) and **Holocron** (Phase 2 KG enrichment from full-corpus chunks). A future "relationship graph rebuilder" workflow and the OpenAI Batch submit/check/cleanup loops have the same shape.

We need a single architectural pattern for these so they share durability, observability, and failure semantics rather than each reinventing the plumbing.

## Options Considered

### Option A: Hangfire background jobs

Use the Hangfire scheduler that's already wired into the Admin app (ETL phases run this way).

**Rejected because:**

- Hangfire models work as a single delegate that runs to completion. Multi-stage workflows with cross-stage state become a tangle of nested `BackgroundJob.ContinueJobWith` calls, with no first-class concept of executor inputs/outputs or per-stage retries.
- Hangfire has no native LLM streaming integration — exposing live progress to the Frontend requires hand-rolling pub/sub.
- The work itself is API-tier (the Frontend calls the API directly, the user expects the response on the page that initiated it). Routing through Admin's Hangfire instance adds a process hop and a deploy boundary for no benefit.
- We already have **Microsoft.Agents.AI.Workflows** in the API tier for the same use case.

### Option B: Plain async Task with an in-memory progress hub

Run the work as a `Task` started from an API endpoint, post progress events to an in-memory subscriber list, let SignalR/SSE forward them to the Frontend.

**Rejected because:**

- Loses durability on process restart. A 25-minute Anakin enhancement run that dies at minute 23 has to start from scratch.
- No first-class checkpoint shape — every workflow ends up reinventing "where did we stop?" with ad-hoc Mongo writes.
- No retry / resumption semantics — the same problem CharacterTimelines hit before being moved onto Workflows.

### Option C: Microsoft.Agents.AI.Workflows with two-layer durability (chosen)

Build pipelines as a graph of `Executor` nodes assembled with `WorkflowBuilder`, run as a `StreamingRun`, persisted via `MongoCheckpointStore`. Pair the framework checkpoint with a per-iteration Mongo side-channel ledger for the heavy artifacts the framework checkpoint must not carry.

## Decision

**Option C — long-running AI work uses `Microsoft.Agents.AI.Workflows` with two-layer durability.**

The pattern is documented below and applies to the existing `CharacterTimelineService` and `HolocronEnhancementService`, plus any future workflow of similar shape (Phase 2 batch loops, future relationship rebuilder, etc.).

### 1. Workflow shape

A workflow is a sequence of `Executor<TIn, TOut>` classes wired together with `WorkflowBuilder.AddEdge(...)`:

```csharp
var workflow = new WorkflowBuilder(discovery)
    .AddEdge(discovery, bundler)
    .AddEdge(bundler, extractor)
    .AddEdge(extractor, consolidator)
    .AddEdge(consolidator, apply)
    .Build();
```

Each executor is a single responsibility. State **between** executors flows as the typed message; state **within** an executor's iteration is the executor's own concern.

### 2. Two-layer durability

Workflows have two parallel persistence channels, and they are **not interchangeable**:

| Layer | Owner | What lives here | Size budget |
| --- | --- | --- | --- |
| **Framework checkpoint** | `MongoCheckpointStore` via `OnCheckpointingAsync` | Workflow position + lightweight executor state (counters, summary, refs to heavy artifacts) | Single Mongo doc — **hard 16 MB ceiling** |
| **Per-iteration ledger** | Executor's own `IMongoCollection<T>` | Heavy artifacts that grow with input size — chunk text, full LLM responses, evidence excerpts | Unbounded, but indexed and partitioned by `JobId` |

**Rule:** if an artifact grows with corpus / batch / iteration count, it lives in the ledger. The framework checkpoint stores at most the **identity** of those artifacts (chunk IDs, batch indices, completed-batch counts).

The Holocron Anakin run (2520 chunks) crossed the 16 MB checkpoint ceiling when it tried to keep `ChunkText` in workflow state; the fix was projecting `HolocronChunkRef` (no text) into the workflow message and letting the extractor rehydrate text per batch from `search.chunks`. Future workflows must follow the same rule by default.

### 3. Idempotency stamping

Every workflow run gets a `JobId` (`ObjectId.GenerateNewId()`). Every persisted artifact the workflow writes — proposals, applied edges, applied enrichments, side-channel ledger rows — carries that `JobId`. Combined with **partial unique indexes** scoped to `JobId`, this gives us:

- **Resumption safety** — re-running an executor that already wrote rows for this `JobId` is a no-op (insert silently dropped via `BulkWriteException` E11000 filtering).
- **Forensics** — every row is traceable to the run that produced it. Enrichment views surface `JobId` and `AgentVersion` so the UI can show "last updated by Holocron job X on date Y."
- **Cleanup** — a failed or aborted run is identified by `JobId`; cleanup is a single delete-by-`JobId` per ledger collection.

Partial filter expressions in Mongo only support `$exists / $eq / $gt / $gte / $lt / $lte / $type / $and / $or`. The "non-empty string JobId" predicate is `{jobId: {$type: "string", $gt: ""}}`, **not** `{$ne: ""}` (rejected by the server). See [feedback memory `mongo_partial_filter_ops`].

### 4. Failure surface

Workflow runs are consumed via `await using var run = await StreamingRun.RunAsync(...)` (the `await using` matters — it disposes the run handle on cancellation / exception so the Mongo checkpoint doc isn't left half-written). The streaming event loop captures **both** failure shapes:

- `ExecutorFailedEvent` — single executor threw. The workflow may continue with other branches; we record the failure and surface it in the activity log.
- `WorkflowErrorEvent` — terminal workflow failure. The `JobStatus` is set to `Failed` with the error message; the UI's stepper renders the failure on the offending step.

**Never** swallow these events. The activity log is the user's only debug surface for a 25-minute headless run; missing the actual failure reason is worse than the failure itself.

### 5. Live progress

Each executor calls `JobService.AppendActivityAsync(jobId, ...)` for user-visible events, and `JobService.SetStatusAsync(...)` at stage transitions. The Frontend polls the job document on a short interval (currently 1.5s) — **no SignalR, no pub/sub, no in-memory subscribers**. Polling is good enough at the granularity humans care about and survives every kind of process boundary for free.

### 6. View access

Workflow output collections (`kg.enrichments`, `kg.edge_enrichments`, etc.) are commonly read through `*.enriched` views with `$lookup` joins. **Pagination through such views is banned** — the optimiser cannot push the consumer's `$match` / `$skip` / `$limit` through a `$lookup` whose `$expr` references outer-doc variables. Paginated reads use the two-phase pattern (paginate base collection by ID, bulk-fetch joined data by `$in`, attach in C#) — see `KnowledgeGraphQueryService.BrowseTemporalNodesAsync` for the canonical example. Single-document fetches (`Find(_id = X)`) through the view are fine.

### 7. Wire-shape decisions

Per-type, not global. The workflow status enum (`HolocronJobStatus`, `CharacterTimelineStatus`, etc.) is decorated `[JsonConverter(typeof(JsonStringEnumConverter))]` directly on the type so it serialises as `"Completed"` on the wire. **Do not** add `JsonStringEnumConverter` via `AddJsonOptions` globally — it broke the Timeline page's `Demarcation` enum on first attempt because the Frontend `HttpClient` deserialiser had no matching converter.

## Rationale

- **Microsoft.Agents.AI.Workflows is already on disk** — the `CharacterTimelineService` migration to it predates the Holocron work and proved the pattern. Adopting the same pattern for Holocron means one shared shape, one shared `MongoCheckpointStore`, one shared `JobService` activity-log abstraction.
- **Two-layer durability is non-negotiable** — anything else either blows the 16 MB checkpoint ceiling on real-sized runs, or loses work on restart. The Anakin run made this concrete.
- **Idempotency stamping** is the cheapest insurance we have. The cost is one ObjectId field per row and a partial unique index; the benefit is that retry / resume / re-run is always safe by construction.
- **Polling > push for headless background work**. Push-based progress (SignalR) trades simplicity for fidelity that doesn't matter at 1-second resolution and breaks across process boundaries we care about.

## Consequences

**Positive:**

- One shape for all long-running AI work — same checkpointing, same activity log, same job UI.
- Process-restart-safe by default. Resume picks up at the last checkpoint.
- Idempotent by construction. Retries and re-runs don't double-write.
- Independent failure of a single executor / batch doesn't kill the run.

**Negative / Trade-offs:**

- The 16 MB checkpoint ceiling is an invariant every workflow author has to keep in mind. Discipline-based, not framework-enforced — a future executor that puts heavy data into workflow state will only fail when corpus size grows past the threshold.
- Polling progress is not "real-time" — there's a 1–2 second lag in the activity log. Acceptable for headless runs that take minutes; would not be acceptable for chat.
- Duplicate-key handling on bulk writes requires catching `MongoBulkWriteException` and filtering E11000 codes. Boilerplate the workflow authors copy from `HolocronApplyExecutor.BulkInsertTolerantAsync`.

## References

- [Design-020](../../specs/020-holocron-async-pipeline/spec.md) — Holocron async pipeline (canonical implementation of this ADR)
- [ADR-002](002-ai-agent-toolkits.md) — conversational agent pattern (the *short-lived* counterpart to this ADR)
- [feedback memory `workflow_checkpoint_size`] — 16 MB ceiling and the refs-not-bodies rule
- [feedback memory `mongo_view_pagination`] — view-with-`$lookup` pagination ban
- [feedback memory `mongo_partial_filter_ops`] — partial filter operator subset
- [feedback memory `jsonstringenumconverter_per_type`] — per-type vs global converter scope
