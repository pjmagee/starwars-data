# Design: Developer-Onboarding Data Snapshot

**Status:** Implemented 2026-05-19 — `src/StarWarsData.SnapshotRestore/make-snapshot.{sh,ps1}`,
`src/StarWarsData.SnapshotRestore/` run-once container, Development+run-mode
gated `snapshot-restore` resource in the AppHost, `ONBOARDING.md`. AppHost +
solution **compile clean** (verified via the pre-commit `dotnet build` +
Unit/Integration test gate). **Not yet verified end-to-end** — no snapshot has
been published and the restore container has not been run against a live Mongo
(see "Validation status").
**Date:** 2026-05-19
**Author:** Patrick Magee + Claude
**Companion docs:** [Design-033](033-prod-to-dev-data-refresh.md) (raw-only,
recurring QA refresh — the *opposite* trade-off), [Design-010](010-mongodb-migration-environment-promotion.md),
[ADR-005](../adr/005-mongodb-migration-strategy.md)

## Problem

A new developer cloning the repo has an empty database. The two existing paths
to data are both unsuitable for onboarding:

- **Full ETL re-crawl** — hours of pipeline runs *and* real OpenAI spend
  (embeddings, character timelines, Holocron), plus hammering the live
  Wookieepedia API from every dev box.
- **Design-033 prod→dev** — deliberately copies *only* `raw.pages` and rebuilds
  the rest via ETL. Correct for *QA against fresh raw content*; wrong for
  onboarding (still pays the full rebuild cost).

We want: clone → restore a snapshot → add your own OpenAI key → run. No ETL, no
embedding spend.

## Goals

1. A single artifact a developer can restore that contains **everything needed
   to run the app** — `raw.*`, `kg.*`, `timeline.*`, `search.*` (incl.
   embedding vectors), `galaxy.*`, `genai.*`, `territory.*`.
2. **No OpenAI spend on restore.** Embeddings are document fields and survive
   `mongodump`; only the Atlas vector-index *definitions* need recreating
   (keyless, via existing Aspire commands).
3. **Privacy/GDPR-safe.** Exclude `chat.*` (user conversations), `admin.*`,
   `hangfire.*`. Real user data never lands on a dev laptop.
4. **Automated for a fresh clone.** The AppHost pulls + restores on first run.
5. **Cannot touch prod.** Restore is dev-only and hard-refuses any DB whose
   name contains "prod".

## Non-Goals

- Replacing Design-033 (still the right tool for recurring raw-content QA).
- Snapshotting user/operational data.
- Capturing Atlas Search / vector-search index *definitions* (`mongodump`
  cannot; recreated post-restore via "Ensure All Indexes" + "4b. Create Index
  Embeddings").
- Aspire **fully managing the local Mongo container** — see Open Questions.

## Design

### Snapshot (maintainer)

`src/StarWarsData.SnapshotRestore/make-snapshot.{sh,ps1}` →
`mongodump --db=starwars-prod --excludeCollectionsWithPrefix=chat./admin./hangfire. --gzip --archive`.
One `.gz` (~10–15 GB gzip'd, from a ~29 GB DB). **Run by the maintainer only,
on the Unraid box** — the prod connection string is a maintainer-held secret
that never ships in the repo or to devs; the dump is intentionally a human,
off-CI operation (CI must never resolve a filled prod secret — `feedback_ci_no_filled_env`).
Running it on the box means `make-snapshot` writes straight into the copyparty
volume — **no upload step**.

### Transport decision: self-hosted copyparty (chosen)

Snapshots are served by **copyparty** on the Unraid host —
`http://192.168.1.102:3923/<vol>/…` (LAN/VPN) and
`https://copyparty.magaoidh.pro/<vol>/…` (public, behind Cloudflare).
`snapshot-url` is a secret AppHost parameter, so a password-gated volume
(`?pw=` / basic-auth in the URL) is fine.

- **Chosen** because the data already lives on this box (zero-cost, no upload,
  full maintainer control), onboarding is infrequent, and copyparty serves raw
  bytes to `curl` (clean direct URLs; HTML listing only to browsers).
- **Caveats:** the public URL traverses Cloudflare, which can 524 on a ~15 GB
  pull over a slow link (CF won't cache an object this large on the free tier,
  so it doesn't offload the home uplink either) — devs should prefer the LAN
  URL. `restore.sh` mitigates with `curl -fL -C - --retry 5` (resumes a partial
  transfer) and `gzip -t`-validates before restore.
- **Rejected:** personal OneDrive / Google Drive — an unauthenticated container
  cannot download from them (verified: `1drv.ms` edit/folder link
  301→`…&migratedtospo=true`→`403`; legacy `api.onedrive.com/v1.0/shares` →
  `unauthenticated`; Graph `shares` needs OAuth).
- **Revisit when:** onboarding scales or devs are mostly remote — move the
  artifact to Cloudflare R2 (~$0.40/mo, zero egress); no code change, just the
  `snapshot-url` value.

### Restore (developer, automated)

`src/StarWarsData.SnapshotRestore/` — a run-once container mirroring the
`mongodb-migrations` pattern (`AddDockerfile`, runs and exits). `restore.sh`:

- no-op if `SNAPSHOT_URL` unset (Option A: dev on the shared server);
- idempotent: skips if `<TARGET_DB>.raw.pages` already populated (unless `FORCE`);
- hard guard: aborts if `TARGET_DB` contains "prod";
- `curl` the archive → `mongorestore --gzip --archive --nsFrom='starwars-prod.*' --nsTo='<TARGET_DB>.*' --drop`.

Wired in `AppHost/Program.cs` under
`builder.Environment.IsDevelopment() && builder.ExecutionContext.IsRunMode` so
it is **never** present in `aspire publish`/`prepare`/`deploy` output.
`mongoDbMigrations.WaitForCompletion(snapshotRestore)` enforces ordering —
restore's `--drop` must land before idempotent migrations apply schema/indexes.

### Index rebuild (developer, one-time, keyless)

Documented in `ONBOARDING.md`: run the existing Aspire `admin` commands
"Ensure All Indexes" then "4b. Create Index Embeddings". Embeddings are already
present in the restored docs, so this is keyless and fast.

### Automation decision: Unraid cron, not the AppHost (chosen)

Snapshot creation is scheduled via `unraid-snapshot-cron.sh` in the Unraid
**User Scripts** plugin (weekly), not an AppHost resource/command.

- **Chosen** because it runs on the box where prod Mongo + copyparty already
  live: the dump is written straight into the copyparty volume (no upload,
  `COPYPARTY_ADMIN` not needed), no WAN double-transfer, and it publishes
  atomically to a stable filename so `snapshot-url` is set once and a dev can
  never fetch a half-written archive. Retention + `flock` included.
- **Rejected: putting it in the AppHost.** The AppHost is the developer-facing
  orchestrator and the source of `aspire publish`/`prepare`/`deploy` output.
  An automated dump+upload there would (a) ride every `aspire run`, only ever
  working for the maintainer; (b) thread a second prod-adjacent secret
  (`COPYPARTY_ADMIN`) through the prod-critical file that already leaked
  secrets once via a release artifact (`feedback_ci_no_filled_env`); (c) force
  a 29 GB-down / 15 GB-up WAN round-trip from a laptop instead of a box-local
  write. Wrong layer, more risk, worse performance.
- **Revisit when:** the box stops co-hosting prod + copyparty, or snapshots
  need to originate off-box — then the documented `COPYPARTY_ADMIN` HTTP-upload
  path (or a job in the Admin app, which already runs on the box with prod
  creds) becomes the better fit.

## Validation status

- **Compiles:** the AppHost change + whole solution build clean and the
  Unit/Integration test tiers pass — verified by the pre-commit hook
  (`csharpier` + `dotnet build` + tests) on commit `dad2763e49`.
- **Not executed end-to-end:** `make-snapshot` and `restore.sh` have not been
  run (no snapshot published; this sandbox can't reach the LAN Mongo). The
  run-once container, the `WaitForCompletion` ordering, the `gzip -t` guard,
  and the prod-write guard are unproven against a live system. **Run once on a
  real fresh clone before this is trusted.**

## Open Questions

1. **Should Aspire fully manage the local Mongo container?** Today Mongo is
   external by deliberate decision (CLAUDE.md: "not Aspire-managed"). True
   one-command onboarding would have the AppHost run a
   `mongodb/mongodb-atlas-local` container in Development. That is a real
   architectural change to a prod-critical file and warrants its own ADR — it
   was **intentionally not done here**. ONBOARDING.md documents the manual
   `docker run` instead. Recommend: spike it behind the same
   Development+RunMode gate, write the ADR, then revisit.
2. ~~**Snapshot refresh cadence / staleness.**~~ **Resolved** — scheduled
   weekly via `unraid-snapshot-cron.sh` (Unraid User Scripts). See "Automation
   decision" above. The prod secret stays on the box, by design.
3. **Archive size.** `starwars-prod` is ~29 GB on disk; the gzip archive is
   several GB (embeddings compress poorly). If download time becomes a problem,
   consider a curated subset or splitting embeddings into an optional layer.

## Incidental fix

While investigating, found **CLAUDE.md states production DB is `starwars`; it
is actually `starwars-prod`** (and dev is `starwars-dev`, confirmed via
`appsettings.{Development,Production}.json` and a live `list-databases`).
CLAUDE.md and any docs referencing `starwars` as the prod DB are corrected in
the same change.
