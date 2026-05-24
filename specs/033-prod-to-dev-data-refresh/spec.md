# Design: Prod → Dev Raw Data Refresh

**Status:** Implemented & verified 2026-05-18 — `ProdToDevSyncService` (server-side `$merge`, hard prod-write guard), `POST /api/admin/sync/prod-to-dev?since=&wipe=` (full control, endpoint-only) plus a dedicated clean `POST /api/admin/sync/prod-to-dev/recent` route, and the `↩ Pull Prod → Dev (raw, last 14d)` Aspire command on the `admin` resource. Open Questions resolved per their proposed defaults: additive `$merge` by default with explicit `?wipe=true` for a true mirror (Q1); full-mirror is endpoint-only, only the 14d slice is a dashboard one-click (Q2); `raw.job_state` left untouched (Q3). **Verified against a live isolated AppHost (both DBs on one `mongod`):** the `since=60` slice merged 18,552 prod pages into `starwars-dev` server-side — a sentinel field planted on a dev doc was overwritten by prod's version (proving `whenMatched: replace`), counts stayed 224,796/224,796 (additive, no loss, prod untouched); the Aspire dashboard command executed end-to-end (`mode=since-14d`, slice empty as expected given stale test data).

**Implementation deviation from the draft below:** the draft's Aspire command used `path: "/api/admin/sync/prod-to-dev?since=14"`. Aspire's `WithHttpCommand` treats the whole `path` literally and URL-encodes the `?`, so the server never sees a query string and returns 404. Fixed by adding a dedicated clean route `POST /api/admin/sync/prod-to-dev/recent` (hardcodes the 14-day slice, no wipe) that the Aspire command targets — consistent with every other plain-path admin command and with Q2 (full-mirror/wipe stays on the query-param route, off the one-click surface). The code blocks below are kept as the original rationale; the shipped controller has both routes.
**Date:** 2026-05-18
**Author:** Patrick Magee + Claude
**Companion docs:** [Design-010](../010-mongodb-migration-environment-promotion/spec.md) (the forward, dev→prod direction), [ADR-005](../../eng/adr/005-mongodb-migration-strategy.md)

## Problem

The daily incremental wiki sync (`PageDownloader.IncrementalSyncAsync`, Hangfire `daily-incremental-sync` at 03:00 UTC — [PageDownloader.cs:635](../../src/StarWarsData.Services/Pages/PageDownloader.cs#L635)) only runs in the **Production** environment, against the `starwars-prod` database. The dedicated server hosts both databases (`starwars-prod` and `starwars-dev`) on **the same MongoDB instance, same connection string, same credentials** — only `Settings.DatabaseName` differs ([Program.cs:28-29](../../src/StarWarsData.AppHost/Program.cs#L28-L29), [Program.cs:76](../../src/StarWarsData.AppHost/Program.cs#L76)).

That means `starwars-dev` slowly goes stale: it never receives the fresh, real Wookieepedia content that prod accumulates. We want to QA and test ETL / Holocron / KG behaviour against *recently updated real articles*, but the only way to get them into dev today is a full re-crawl (every dev box hammering the live MediaWiki API) or manual single-page downloads.

We are explicitly **not** trying to mirror prod's derived state into dev. The point of fresh raw content in dev is to *exercise dev's ETL/Holocron code against it*. Copying `kg.*` / `timeline.*` / `search.*` would defeat that.

This is the mirror image of [Design-010](../010-mongodb-migration-environment-promotion/spec.md): 010 promotes *schema/structure forward* (dev → prod via tracked migrations); this design pulls *content backward* (prod → dev). They are complementary and do not overlap. Design-010's "Future Work" already anticipated this: *"Admin dashboard integration — admin endpoints triggered from the Aspire dashboard alongside ETL phases."*

## Goals

1. **One ad-hoc operation** an operator triggers on demand — not a recurring job.
2. **Copy only the source of truth** — `raw.pages` (the sole non-derivable collection; everything else is rebuilt by existing ETL phases).
3. **Two modes from one endpoint**: a *recent slice* (changed pages within the last N days — the QA case) and a *full mirror* (reset dev to match prod).
4. **Server-side copy** — the data never transits any client process (C# *or* mongosh); `mongod` reads prod and writes dev internally.
5. **Hard prod-write guard** — prod is read-only in this flow; the operation refuses to run if its write target is `starwars-prod`.
6. **Surfaced where every other ETL trigger lives** — an Aspire HTTP command on the `admin` resource + the Admin dashboard.

## Non-Goals

- Copying derived collections (`kg.*`, `timeline.*`, `search.*`, `galaxy.*`, `genai.*`, `suggestions.*`). These are rebuilt in dev via the existing Phase 5 → 3a → 4a → 8 → 9 Aspire commands.
- Copying user/operational data (`chat.*`, `admin.*`).
- Any dev → prod data movement (that is Design-010's territory).
- Replacing the daily incremental sync. This is a supplement for dev, not a change to prod's sync.

## Why these choices

### Server-side `$merge` aggregation — no client round-trip

The decisive property of this copy is that prod and dev are the **same `mongod` deployment**. So the copy should never move documents through *any* client:

- A batched C# cursor (`Find` → `BulkWrite`) streams every document server → app → server. Bounded memory, but a pointless network round-trip on a same-server copy.
- `mongodump --archive | mongorestore --nsFrom --nsTo` (the documented cross-DB copy in [Design-010:35-43](../010-mongodb-migration-environment-promotion/spec.md)) has the same shape: server → dump process → pipe → server.
- A mongosh `.find().forEach(insert)` loop: server → mongosh → server. Same waste.

Instead, a single **`$merge` aggregation** runs the read *and* the write inside `mongod`:

```js
db.getSiblingDB('starwars-prod').getCollection('raw.pages').aggregate([
  { $match: { lastModified: { $gte: cutoff } } },   // omit stage entirely for full mirror
  { $merge: {
      into: { db: 'starwars-dev', coll: 'raw.pages' },
      on: '_id',                  // pageId IS _id ([BsonId]) — _id is always uniquely indexed
      whenMatched: 'replace',
      whenNotMatched: 'insert'
  } }
])
```

No document enters the C# process or mongosh — the client issues one command and waits. This is strictly better than the cursor copy, `mongodump`, and the mongosh loop. `$merge` is a core aggregation stage (introduced in MongoDB 4.2; the deployment here is well past that).

**Verified against the [official `$merge` documentation](https://www.mongodb.com/docs/manual/reference/operator/aggregation/merge/) (checked 2026-05-18):**

- **Community Edition is supported** — the docs explicitly list *"MongoDB Community: The source-available, free-to-use, and self-managed version of MongoDB"* among the environments `$merge` runs in. This is not an Atlas/Enterprise-only feature.
- **Cross-database output is a documented first-class form** — *"The database and collection name in a document to output to a collection in the specified database. For example: `into: { db:"myDB", coll:"myOutput" }`"* — exactly the `into: { db: targetDbName, coll: … }` shape used below.
- **`whenMatched: "replace"` and `whenNotMatched: "insert"`** are both documented valid values.

**Why the C# driver still issues it (not a mongosh container):** the transport is irrelevant since no data flows through it, so we choose the path that stays observable in Aspire traces/structured logs and needs no extra image — `IMongoCollection<Page>.Aggregate(...)` with a `$merge` stage, from the existing `IMongoClient`. The same client already reaches both databases: `GetDatabase("starwars-prod")` (read source) and `GetDatabase(settings.DatabaseName)` (the `$merge` target db). `mongodump | mongorestore` is kept only as a documented manual fallback for a full cold mirror.

**Unique-index requirement — satisfied for free by merging on `_id`.** The docs are explicit: *"`$merge` requires a unique index with keys that correspond to the on identifier fields."* This is a real prerequisite, not a non-issue. We avoid having to create or maintain any index because `Page.PageId` is `[BsonId]` ([Page.cs:8-10](../../src/StarWarsData.Models/Pages/Page.cs#L8-L10)) — the merge key is therefore `_id`, which always carries an automatic unique index. **If a future change merged on a non-`_id` field, a unique index on that field in the dev target would become a hard prerequisite.** (BSON field names are camelCase — the `$match` filters on `lastModified`, not the C# property name.)

### Ad-hoc Aspire command, not a Hangfire recurring job

The operator wants this on demand for QA, and the Aspire dashboard is already the home of every ETL trigger ([Program.cs:82-246](../../src/StarWarsData.AppHost/Program.cs#L82-L246)). It runs as a one-shot Hangfire **background job** (not recurring), enqueued the same way as the ETL phase endpoints, guarded by the existing `IsJobAlreadyActive` check ([AdminController.cs:22-59](../../src/StarWarsData.Admin/Features/Admin/AdminController.cs#L22-L59)) so it can't double-run.

## Design

### New service: `ProdToDevSyncService`

Location: `src/StarWarsData.Services/Admin/ProdToDevSyncService.cs` (sits next to `JobToggleService`).

```csharp
public sealed class ProdToDevSyncService(
    IOptions<SettingsOptions> settings,
    IMongoClient mongoClient,
    ILogger<ProdToDevSyncService> logger)
{
    private const string ProdDb = "starwars-prod";

    public async Task<ProdToDevSyncResult> RefreshRawPagesAsync(
        int? sinceDays, CancellationToken ct)
    {
        var targetDbName = settings.Value.DatabaseName;

        // GUARD: never write prod from this flow.
        if (string.Equals(targetDbName, ProdDb, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Prod→Dev refresh refuses to run when the target database is starwars-prod.");

        var source = mongoClient.GetDatabase(ProdDb)
                                 .GetCollection<Page>(Collections.Pages);

        // Server-side $merge: mongod reads prod and writes dev internally.
        // No document transits this process.
        var pipeline = new List<BsonDocument>();
        if (sinceDays is { } d)
            pipeline.Add(new BsonDocument("$match",
                new BsonDocument("lastModified",
                    new BsonDocument("$gte", DateTime.UtcNow.AddDays(-d)))));
        pipeline.Add(new BsonDocument("$merge", new BsonDocument
        {
            ["into"] = new BsonDocument { ["db"] = targetDbName, ["coll"] = Collections.Pages },
            ["on"] = "_id",
            ["whenMatched"] = "replace",
            ["whenNotMatched"] = "insert",
        }));

        await source.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct);
        // $merge yields no documents; the cursor completes when the server-side write finishes.
    }
}
```

Key points:

- **Guard first.** The `targetDbName == "starwars-prod"` check is the first statement and throws before the pipeline is built or executed.
- **`$merge` on `_id`** (= `PageId`), `whenMatched: replace` / `whenNotMatched: insert` — additive and idempotent. A recent-slice refresh layers onto existing dev content; re-running with a wider `since` converges. It does **not** delete dev pages absent from the slice (true-mirror deletes are an explicit opt-in — see Open Questions).
- **No client-side batching needed** — there is no client-side data path. `mongod` handles the read+write internally; memory pressure is the server's, not the app's.
- After the copy, optionally **reset `raw.job_state`** so dev's own incremental cursor ([RecentSyncStatus](../../src/StarWarsData.Models/Pages/RecentSyncStatus.cs)) starts clean. Default: leave it untouched (the copy is not a wiki sync and shouldn't masquerade as one in the Dashboard's "Recently Synced" panel).

### Endpoint

`AdminController` ([api/admin route prefix](../../src/StarWarsData.Admin/Features/Admin/AdminController.cs#L9-L10)):

```csharp
[HttpPost("sync/prod-to-dev")]
public IActionResult RefreshFromProd([FromQuery] int? since)
{
    if (IsJobAlreadyActive(typeof(ProdToDevSyncService), nameof(ProdToDevSyncService.RefreshRawPagesAsync)))
        return Conflict(new { error = "A prod→dev refresh is already running." });

    var jobId = BackgroundJob.Enqueue<ProdToDevSyncService>(
        s => s.RefreshRawPagesAsync(since, CancellationToken.None));
    return Accepted(new { jobId, mode = since is null ? "full" : $"since-{since}d" });
}
```

- `?since=14` → recent slice (default the dashboard command sends).
- `?since` omitted → full mirror.
- Returns `409 Conflict` if already running; `Accepted` with the Hangfire job id otherwise. Progress is visible in the Hangfire dashboard like every other ETL phase.

### Aspire command

Added to the `admin` resource alongside the existing ETL commands ([Program.cs:82+](../../src/StarWarsData.AppHost/Program.cs#L82)):

```csharp
.WithHttpCommand(
    path: "/api/admin/sync/prod-to-dev?since=14",
    displayName: "↩ Pull Prod → Dev (raw, last 14d)",
    commandOptions: new HttpCommandOptions
    {
        Method = HttpMethod.Post,
        Description = "Copies raw.pages changed in prod within the last 14 days into the dev "
                    + "database. Read-only against prod; refuses to run if target is starwars-prod. "
                    + "Run Phase 5 → 3a → 4a afterward to rebuild dev's derived data.",
        IconName = "DatabaseArrowDown",
        IsHighlighted = false,
    }
)
```

A second command without the query string (or `?since` omitted) can expose the full-mirror mode if desired, or that stays endpoint-only to keep the destructive case off the one-click dashboard.

## Operator workflow

```text
1. (dev environment, Aspire dashboard)
   Run "↩ Pull Prod → Dev (raw, last 14d)"        → raw.pages in starwars-dev refreshed
2. Run "5. Build Infobox Graph"                    → kg.* rebuilt from new raw.pages
3. Run "3a. Build Timeline Events (from KG)"       → timeline.* rebuilt
4. Run "4a. Run Article Chunking"                  → search.chunks + embeddings rebuilt
5. (optional) Phase 8 / 9, Holocron pass           → galaxy/suggestions/enrichment QA
```

Steps 2–5 are exactly the existing Aspire commands — this design adds only step 1. The downstream-invalidation logic in `PageDownloader.InvalidateDownstreamAsync` ([PageDownloader.cs:955](../../src/StarWarsData.Services/Pages/PageDownloader.cs#L955)) is **not** triggered by the raw copy; the operator rebuilds explicitly so the QA run is deterministic and observable.

## Safety & Risk

| Risk | Mitigation |
| --- | --- |
| Accidentally writing prod | Guard throws if `Settings.DatabaseName == "starwars-prod"` before any target handle is taken. The dev-environment Admin app is configured with `starwars-dev` ([Program.cs:76](../../src/StarWarsData.AppHost/Program.cs#L76)); the command is only meaningful when run there. |
| Mutating prod source | Reads only — no write API is ever called against `GetDatabase("starwars-prod")`. |
| Double-run / overlap | `IsJobAlreadyActive` guard + `409 Conflict`, same as ETL phases. |
| Dev divergence after partial slice | `$merge` on `_id` is additive and idempotent; re-running with a wider `since` converges. Full mirror brings every prod page; a true mirror (also removing dev-only pages) is an explicit opt-in. |
| Stale derived data after copy | Operator workflow explicitly rebuilds (steps 2–5). Document the required phases in the command description. |

## Open Questions

*All resolved at implementation (2026-05-18) per their proposed defaults — kept here for the rationale.*

1. **[Resolved: implemented as proposed]** **True-mirror semantics** — `$merge` never deletes, so dev pages absent from prod (or from the slice) linger. Should a `?wipe=true` flag `DeleteMany({})` dev's `raw.pages` before the `$merge` (true mirror, removes upstream-deleted pages), accepting that it briefly empties the collection? Proposed: additive `$merge` by default; explicit `?wipe=true` for a true full mirror only.
2. **[Resolved: endpoint-only]** **Expose full-mirror as a dashboard one-click**, or endpoint-only? Proposed: endpoint-only to keep the heavy/destructive case off the single-click surface.
3. **[Resolved: left untouched]** **`raw.job_state` handling** — leave dev's cursor untouched (proposed) vs. seed it from prod so a subsequent dev incremental sync continues from prod's position. Leaving it untouched avoids the Dashboard "Recently Synced" panel misreporting a copy as a wiki sync.

## Future Work

- A symmetric *recent-slice diff report* (which prod pages would land in dev for a given `since`) before committing the copy.
- Extend the same guarded-copy primitive to optionally bring a named subset of pages (by title/category) for targeted bug reproduction.
