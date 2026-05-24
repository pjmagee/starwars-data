# Design-040: Holocron rate limits, queue & undo controls

**Status:** Proposed.
**Date:** 2026-05-20
**Author:** Patrick Magee + Claude
**Related:** [Design-018 Holocron skeleton](../018-holocron-skeleton/spec.md), [Design-020 Holocron async pipeline](../020-holocron-async-pipeline/spec.md), [Design-021 Edge-bound provenance](../021-edge-bound-provenance/spec.md), [Design-022 SP-4 sidebar](../022-galaxy-map-copilot/spec.md), [ADR-006 long-running AI workflows](../../eng/adr/006-long-running-ai-workflows.md)

## Problem

The Holocron AI is the most expensive operation on the site — each run hits the OpenAI API, walks the knowledge graph, and reads article chunks. Today there is **no** rate-limit on it, **no** cap on how many can run at once, **no** public way to see what's queued or how long the wait will be, and **no** way to undo what it added if a run produced bad data.

Four concrete gaps the project is now exposed to:

1. A single user can hammer "Enhance with Holocron" on the same node and stack OpenAI cost with no protection. Same for "Regenerate character timeline" if/when it gets a manual trigger.
2. Multiple jobs can fan out and saturate the OpenAI rate limit / blow the per-day budget, with no admission control.
3. The `/holocron/jobs` list shows individual job state but there is no surface for *the queue* — what's waiting, in what order, why a request hasn't started yet — which we'll want both for user reassurance and for the maintainer to spot a backlog.
4. If a Holocron run goes wrong on a specific node (bad inference, factual drift), there is no in-product way to revert that node to its pre-Holocron state. The data sits there until manually scrubbed from Mongo.

This design treats those as one coherent feature set: **admission control + observability + undo** on the Holocron lifecycle.

## Goals

- A **per-node 24h cooldown** on Holocron runs against the same KG node (`kg.nodes` PageId).
- A **per-character 24h cooldown** on character-timeline regeneration.
- A **concurrency queue** with a configurable cap (default `2`) — additional requests wait in FIFO order rather than running in parallel.
- A **public, read-only `/holocron/queue`** page showing what's running and what's waiting, in the existing **Holocron AI** nav section. Admin-authenticated users can **cancel** queued items or **kill** a running job from the same page.
- A **"Clear Holocron Enhancements"** action for admins, available on `/knowledge-graph` (and `/graph-explorer` for parity) next to the existing **Enhance with Holocron** and **Holocron History** buttons. It reverses the enrichments the Holocron added to a given node.

## Non-goals

- **Per-user quotas.** This is a public, low-traffic site; the per-node and concurrency limits are sufficient. Per-user quotas can come later if abuse data shows it's needed (see *Revisit when*).
- **Distributed/multi-host queue.** The deploy is a single AppHost; an in-process queue backed by Mongo is enough. Switching to a distributed broker is out of scope.
- **Replacing the Hangfire daily Holocron pass.** The daily job (06:00 UTC, [CLAUDE.md](../../CLAUDE.md)) keeps its schedule — it just submits through the same queue as ad-hoc requests, so the cooldown and the cap apply uniformly.
- **Selective undo at edge / claim granularity.** "Clear Holocron Enhancements" is whole-node. Per-claim undo can come later if needed.
- **Confirmed-claim preservation.** A *cleared* node loses all Holocron-added data on that node, including claims a maintainer might have manually marked as good. Selective preservation is a future refinement.

## Design

### 1. Per-node Holocron cooldown (24h)

Tracked on the Holocron job itself. Whenever `HolocronJobService` (cf. `src/StarWarsData.Services/AI/Agents/HolocronJobService.cs`) accepts a request for a `(target_kind="node", target_id=<pageId>)` pair, it first checks whether a **non-failed** job for the same target completed within the last `HolocronCooldownHours` (default `24`). If yes, the request is rejected with a structured error (`HolocronCooldownActive`) carrying the timestamp the cooldown lifts. The Frontend renders that as a disabled **Enhance with Holocron** button with a tooltip ("Available again in 3h 12m").

- Failed jobs do **not** consume the cooldown — the user shouldn't be locked out because the LLM timed out. A failed run is a no-op for the cooldown clock.
- Cancelled or killed jobs (see §4) also do not consume the cooldown.
- The 24h is from the *completion* timestamp, not the *start* — protects against a long-running job artificially extending the window.
- The cooldown also applies to the Hangfire daily pass: if a node was just manually enhanced 2h ago, the daily pass skips it (counted as "deferred", logged at info, no error).
- Implementation hint: a unique index on `(target_kind, target_id, completed_at)` keeps the lookup cheap; the existing `HolocronJobIdUniqueIndexTests` in `src/StarWarsData.Tests/Integration/` is the natural place to add a coverage test for the new index.

### 2. Character-timeline regeneration cooldown (24h)

Same shape, different target. When a "Regenerate character timeline" action runs against a `Character` node, it routes through `HolocronJobService` as a `(target_kind="character_timeline", target_id=<pageId>)` job and uses the same per-target cooldown column. `CharacterTimelineCooldownHours` is a separate setting (default `24`) so we can tune them independently if usage shows one wants a different value.

The Hangfire ETL Phase 5 ("create-character-timelines") bypasses the cooldown for the bulk pass — it has its own admission control in the form of the daily schedule. Only the per-character manual trigger is cooldown-gated.

### 3. Concurrency queue (default cap = 2)

A single in-process FIFO queue inside the API service, backed by a `holocron.queue` Mongo collection so visibility survives restarts. `HolocronMaxConcurrency` (default `2`, in `SettingsOptions`) caps how many job runs execute simultaneously; everything else sits in the queue with a position.

- **Submission path:** `Enhance with Holocron` (KG page), `Regenerate character timeline` (admin/timeline page), and the Hangfire daily pass all call `HolocronJobService.EnqueueAsync(target)`. The service first applies the §1/§2 cooldown check; on accept it inserts a queue row (status `queued`, position = current tail + 1) and signals a worker.
- **Worker pool:** a bounded `SemaphoreSlim(initialCount: HolocronMaxConcurrency)` inside a hosted background service. On startup it scans `holocron.queue` for orphaned `running` rows from a crashed previous process and either resumes them (workflow checkpoints exist — Design-020) or transitions them to `failed` with reason `interrupted`. New jobs are picked in `queued_at` order.
- **Queue position is advisory.** The displayed "position 3 of 5" is computed at read time, not stored — a queued row only stores `queued_at`. ETA is `(position-1) × avg_run_duration` from a 24h rolling average; if average is unknown (cold start), fall back to a static "a few minutes".
- **Hangfire interaction:** the daily pass enqueues per-eligible-node and lets the queue drain at its own pace, instead of running all enhancements in a tight loop. If the daily pass spawns more than the queue can drain before the next day, the leftovers naturally fall to the next pass — that's fine, the corpus changes slowly.

### 4. Public Holocron Queue page (`/holocron/queue`)

A new Blazor page at `src/StarWarsData.Frontend/Components/Pages/HolocronQueue.razor`, added to the **Holocron AI** section of `NavMenu.razor`:

```razor
<MudNavLink Href="/holocron/queue" Match="NavLinkMatch.All"
            Icon="@Icons.Material.Filled.HourglassTop">Holocron Queue</MudNavLink>
```

**Public read-only contents:**

- Two MudTables, **Running** and **Queued** (live-refreshed every 5s, or pushed via the existing SP-4 streaming infra if cheap).
- Columns per row: target (linked to `/graph-explorer/{pageId}`), kind (Node enhancement / Character timeline), submitted at, position (queued) or elapsed (running), ETA.
- Footer summary: current concurrency `(running)/(cap)`, total queued, cooldowns active count.
- No PII — submitters are not tracked or surfaced. Hangfire-submitted jobs are tagged "scheduled" so the user can see what's automated vs. ad-hoc.

**Admin-only actions** (gated by `<AuthorizeView Roles="admin">` — same Keycloak role + dev-auth bypass used elsewhere on the Frontend; see [ADR-001](../../eng/adr/001-internal-api-auth.md)):

- **Cancel** a queued row → removes it (status `cancelled`); does not consume the cooldown.
- **Kill** a running row → instructs the worker to dispose the workflow + cancel its `CancellationToken`, marks the row `killed`. The workflow's existing checkpoint stays so we have a forensics trail; does not consume the cooldown.
- Both go through `POST /api/holocron/queue/{id}/cancel` and `POST /api/holocron/queue/{id}/kill` — auth required, audited (writes a `kg.events` row with `actor=<userId>` and `reason=<text from prompt>`).

Public reads use the existing public-stats pattern (`Cache-Control: public, max-age=5`; Design-037 / ADR-009). No auth required for `GET`.

### 5. Clear Holocron Enhancements

The "undo" action. Available on `/knowledge-graph` and `/graph-explorer` *only* when:

1. The currently authenticated user has the `admin` role, AND
2. The selected node already has at least one Holocron enrichment (otherwise the button doesn't render — nothing to clear).

It sits next to the existing **Enhance with Holocron** and **Holocron History** buttons on the per-node action row in `KnowledgeGraph.razor:268` (and the parity row on `GraphExplorer.razor:102`).

**Behaviour:**

- Confirmation dialog before action (irreversible to the user — preserves audit trail server-side but the UI presents it as "remove all Holocron-added information on this node").
- Server endpoint `POST /api/holocron/nodes/{pageId}/clear-enhancements` (admin-gated).
- **Soft-delete** in three collections, all in one transactional batch:
  - `kg.enrichments` — mark `cleared_at` + `cleared_by` on every doc with `target_node = {pageId}`.
  - `kg.edge_enrichments` — same, for any edge enrichment **whose owning node is** this pageId (a single edge enrichment is owned by exactly one side per Design-021; check the field name in code).
  - `kg.events` — write a `HolocronCleared` event with `actor`, `target_node`, `cleared_count`, `at`.
- Read-side views and Holocron history continue to honour `cleared_at IS NULL` filters — the existing query layer needs one new clause per affected query.
- **Does not** drop the entries from Mongo. Future "uncleared" or per-claim restore is then possible. A separate hard-delete maintenance job can sweep aged-cleared docs later if storage becomes an issue.
- Triggering a *new* Holocron run on a cleared node is allowed (cooldown applies as normal), and a re-run does **not** automatically restore cleared enrichments — it produces new ones. Cleared docs stay cleared.

## Data shape

New collection: **`holocron.queue`** (single source of truth for both queue and history of in-flight items):

```text
_id            ObjectId
jobId          string   — matches the existing HolocronJobService job id
targetKind     string   — "node" | "character_timeline"
targetId       int      — KG node PageId
source         string   — "user" | "hangfire" | "admin"
status         string   — "queued" | "running" | "completed" | "failed" | "cancelled" | "killed"
queuedAt       DateTime UTC
startedAt      DateTime? UTC
completedAt    DateTime? UTC
failureReason  string?
actor          string?  — Keycloak user id when status changed by an admin action; null for system
```

Indexes:

- `{ status: 1, queuedAt: 1 }` — picks the next queued row + lists running.
- `{ targetKind: 1, targetId: 1, completedAt: -1 }` — cooldown lookup (§1/§2). Partial filter on `status: { $in: ["completed"] }` so failed/cancelled/killed don't consume the cooldown (Mongo partial filters can express that; verify exact form — cf. [feedback_mongo_partial_filter_ops](../../CLAUDE.md)).
- `{ jobId: 1 }` unique — already covered by the existing `HolocronJobIdUniqueIndexTests` pattern.

Soft-delete fields on `kg.enrichments` / `kg.edge_enrichments`:

```text
clearedAt      DateTime? UTC
clearedBy      string?         — Keycloak user id
```

The existing read paths filter `clearedAt: null` (or absent — keep both forms working during rollout).

## API surface

Public (anonymous, cached):

- `GET /api/holocron/queue` → `{ running: [...], queued: [...], concurrencyCap, cooldownsActive }`. `Cache-Control: public, max-age=5`.

Admin (Keycloak `admin` role, header-forwarded per ADR-001):

- `POST /api/holocron/queue/{id}/cancel` → 204 when accepted; 409 if it has already started.
- `POST /api/holocron/queue/{id}/kill` → 204; emits a cancellation token to the running worker.
- `POST /api/holocron/nodes/{pageId}/clear-enhancements` → `{ clearedCount }`. Idempotent.

Internal (called by the existing buttons + Hangfire):

- `POST /api/holocron/enhance/{pageId}` (already exists for the Enhance button — extend to consult cooldown + enqueue rather than run inline).
- `POST /api/holocron/character-timeline/{pageId}` (new or existing — verify; routed through the same queue).

Errors carry a structured body so the UI can render specific copy:

```json
{ "code": "HolocronCooldownActive", "availableAt": "2026-05-21T09:14:00Z" }
{ "code": "HolocronQueueFull", "position": 7, "etaSeconds": 480 }   // optional, when cap hit
```

## UI placement

- **Knowledge Graph + Graph Explorer** node row: `Enhance with Holocron` (existing), `Holocron History` (existing), and the new **`Clear Holocron Enhancements`** button gated by `<AuthorizeView Roles="admin">`, rendered with `Color.Error Variant.Outlined` (destructive treatment) + a leading `Icons.Material.Filled.DeleteSweep`. Disabled when the node has no enrichments.
- **Nav** (`NavMenu.razor`): insert `Holocron Queue` between the existing `Holocron Log` and `Holocron Jobs` links, with `Icons.Material.Filled.HourglassTop`.
- **Queue page** layout: hero `MudPaper` ("⌛ N running of M, K queued") + two MudTables. Admin actions appear as a row-trailing icon button group; on non-admin users the buttons collapse into an "Admin actions" hidden region (do not render at all to avoid revealing admin surface).

## Auth model

- Reads: anonymous, public.
- Cancel / Kill / Clear-Enhancements: `admin` Keycloak role. Identity is forwarded from the Frontend via the existing `X-User-Id` `DelegatingHandler` (see [ADR-001](../../eng/adr/001-internal-api-auth.md)). The dev-auth bypass in the Frontend's `Program.cs` injects a synthetic `admin`-role principal so local development without Keycloak still works (see `feedback_dev_auth_bypass`).

## Settings

Add to `SettingsOptions` (`src/StarWarsData.Models/Settings.cs`), all optional with the defaults below:

| Setting | Default | Effect |
| --- | --- | --- |
| `HolocronCooldownHours` | `24` | Per-node node-enhancement cooldown. |
| `CharacterTimelineCooldownHours` | `24` | Per-character regeneration cooldown. |
| `HolocronMaxConcurrency` | `2` | In-process worker semaphore size. |
| `HolocronCleared.HardDeleteAfterDays` | `null` | Optional sweep window for soft-deleted enrichments; null = never. |

Tunable in `appsettings.json` and per-environment overrides; no code change to adjust.

## Hangfire interaction

The daily 06:00 UTC pass enumerates eligible nodes, calls `HolocronJobService.EnqueueAsync` per node with `source = "hangfire"`, then exits. The queue drains under the concurrency cap. Per-node cooldown trims the daily list automatically. No special-casing — the daily and ad-hoc paths converge.

## Telemetry

- OTEL counters: `holocron.enqueued{source,target_kind}`, `holocron.started`, `holocron.completed`, `holocron.failed`, `holocron.cancelled`, `holocron.killed`, `holocron.cleared{by_actor}`.
- Histogram: `holocron.run_duration_seconds{target_kind}` — feeds the queue-page ETA.
- Log everything that mutates queue or enrichment state at `Information`; cooldown-rejected requests at `Information` too (they're not errors).

## Rollout

1. Add settings + `holocron.queue` collection + indexes + soft-delete columns.
2. Wire `HolocronJobService` to enqueue + apply cooldown.
3. Background worker + admin endpoints.
4. Queue page (read-only first), then layer admin actions.
5. `Clear Holocron Enhancements` button + endpoint.
6. Daily Hangfire pass switches to the queued submission path.

Each step is safe in isolation; the queue can ship first with concurrency cap = ∞ behaviour-equivalent to today, then turn the cap down.

## Open questions

- **Queued vs. cooldown-rejected at the daily pass.** When the daily pass hits a node in cooldown, do we want a deferred-list visible in the queue page, or just a log line? Default to log line; revisit if maintainers want the visibility.
- **Per-edge-enrichment ownership.** §5 says edge enrichments are "owned by one side"; confirm the exact field on `kg.edge_enrichments` (likely `ownerNode` per Design-021) before implementation — if both sides are flagged, the clear becomes ambiguous and we need a tie-break rule.
- **Rate-limit error UX on the KG button.** Disabled with tooltip vs. enabled-then-toast? Default disabled; the cost of an extra API round-trip to discover the cooldown each render is small (the data is in `kg.nodes` or its enrichment summary anyway).
- **Cooldown bypass for maintainers.** Should an admin be able to override a node's cooldown? Default no; if a run is genuinely needed sooner the admin can clear-and-rerun.

## Revisit when

- Daily pass volume saturates the queue and the cooldown trim isn't enough — at that point the concurrency cap might need to be raised, or per-node enhancement work split into smaller jobs.
- Per-user abuse appears in logs — then add per-user (or per-IP for anonymous) rate limits on top of the per-node cooldown.
- Cleared enrichments accumulate to the point storage matters — implement the `HardDeleteAfterDays` sweep.
- A second writer is introduced (e.g. a separate Holocron host for parallelism) — promote the in-process queue + semaphore to a distributed primitive (Mongo `findAndModify` lease, or a real broker).
