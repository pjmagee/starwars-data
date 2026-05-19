# Onboarding — run the whole stack locally

Goal: clone the repo and have the full app running against **real data** (the
whole knowledge graph, timelines, embeddings — everything prod has) without
re-running the multi-hour ETL pipeline or spending a cent of OpenAI credit.

The only thing that is *yours* is your OpenAI key. Everything else is
automated by the Aspire AppHost.

---

## 1. Prerequisites

- **.NET 10 SDK** (see `global.json`)
- **Docker** (Desktop on Windows/Mac) — running
- **Aspire CLI** — install via the official script ([aspire.dev](https://aspire.dev/get-started/install-cli/)):
  - Windows (PowerShell): `irm https://aspire.dev/install.ps1 | iex`
  - macOS/Linux (bash): `curl -sSL https://aspire.dev/install.sh | bash`
- An **OpenAI API key** (yours — used only at runtime for chat/agent features)

## 2. MongoDB

**On the LAN/VPN with the shared server?** Nothing to do —
`appsettings.Development.json` already points at it. Skip to step 3.

**Fresh clone, no server access?** Flip `use-local-mongo` to `true` and the
AppHost runs MongoDB for you — an Aspire-managed `mongodb/mongodb-atlas-local`
container with a persistent volume; on first `aspire run` it's created and the
snapshot is restored into it (the volume persists, so it never re-restores).
ADR-010.

It's a normal parameter — you can see it in
`src/StarWarsData.AppHost/appsettings.Development.json` under
`Parameters:use-local-mongo` (default `"false"`), right next to `mongo-host`.
Flip it on with a machine-local user-secret (same convention as every other
parameter in this repo — nothing tracked changes):

```bash
dotnet user-secrets set "Parameters:use-local-mongo" "true" --project src/StarWarsData.AppHost
```

Default (`"false"`) = external server, so existing server-based workflows and
production are completely unaffected.

## 3. Secrets — just your OpenAI key

From `src/StarWarsData.AppHost`:

```bash
dotnet user-secrets set "Parameters:openapi" "sk-..."
```

That's the only required secret. `snapshot-url` already defaults to the shared
copyparty snapshot — you don't set it unless the host/file changes.

### (Reference) the snapshot URL — already the default, no action needed

`snapshot-url` defaults in the AppHost to the shared copyparty file —
`https://copyparty.magaoidh.pro/swdata/starwars-snapshot-latest.gz`. You only
read this section if you need to override it. It must be a **direct file URL**
(not a folder/listing — the restore `gzip -t`-checks and fails fast on HTML):

- **The default:** `https://copyparty.magaoidh.pro/swdata/starwars-snapshot-latest.gz`
  Works from anywhere — a fresh clone is *not* on the LAN and cannot reach a
  `192.168.1.x` address. The restore resumes on drop (`curl -C -`).
- **Optional LAN override (only if you're already on the LAN/VPN):**
  `http://192.168.1.102:3923/swdata/starwars-snapshot-latest.gz` — bypasses
  Cloudflare for full local speed. Not reachable off-network; don't set this
  as the shared default.
- If the copyparty volume is password-protected, put the password in the URL
  (`?pw=…`) or use `https://user:pass@…` — `snapshot-url` is a secret AppHost
  parameter, so it never lands in the repo.

(Personal OneDrive/Google Drive share links were tried and **don't work** for
an unattended container — they 403 / serve HTML to `curl`. copyparty serves the
raw bytes, so it just works.)

## 4. Run it

```bash
aspire run --project src/StarWarsData.AppHost
#   or: dotnet run --project src/StarWarsData.AppHost
```

If `use-local-mongo` is `true`, the first run shows a **`mongodb-local`**
container coming up, then a **`snapshot-restore`** resource that:

1. downloads the snapshot,
2. restores it into `starwars-dev` (renaming `starwars-prod.*` →
   `starwars-dev.*`),
3. exits.

`mongodb-migrations` waits for it, then applies schema/index migrations. It is
**idempotent** — restart the AppHost any time; the restore skips itself once
the DB is populated. The `starwars-dev-mongo` volume persists the data across
restarts, so this whole sequence runs **once**. To force a fresh restore,
remove that Docker volume (`docker volume rm starwars-dev-mongo`) and re-run.

## 5. Rebuild the search/vector indexes (one-time, keyless)

`mongodump` captures collections and regular indexes but **not** Atlas Search /
vector-search index *definitions*. The embedding vectors themselves are in the
restored documents (so **no re-embedding, no OpenAI spend**) — you just need to
recreate the index definitions over them.

In the **Aspire dashboard**, on the `admin` resource, run these commands once,
in order:

1. **Ensure All Indexes**
2. **4b. Create Index Embeddings** (creates the Atlas vector-search indexes)

Until you do this, full-text/semantic search and the RAG tools return empty.

## 6. Verify

- Frontend (port `9081` by default) loads and search returns results → indexes OK.
- Open a chat / ask a question → exercises your OpenAI key.
- Galaxy map / timelines render → KG + derived data restored.

---

## Maintainer: publishing a snapshot

> Full operational how-to (both paths, prerequisites, the Cloudflare-413
> gotcha, verification): [eng/docs/maintainer-snapshot-publish.md](eng/docs/maintainer-snapshot-publish.md).
> The summary below is the on-box happy path.

Only the maintainer can do this — it needs the prod connection string, which is
**never** in the repo or given to devs (same rule as CI: a filled prod secret
must never leave the maintainer's machine). Run it **on the Unraid box** (where
the prod Mongo and copyparty already live), so there is no upload step:

```bash
# On 192.168.1.102, with the prod connection string in MDB_URI:
MDB_URI='mongodb://…@localhost:27018/?authSource=admin&directConnection=true' \
  src/StarWarsData.SnapshotRestore/make-snapshot.sh /path/to/copyparty/<volume>/starwars-snapshot-$(date -u +%Y%m%d).gz
```

That writes the gzip dump (minus `chat.*`/`admin.*`/`hangfire.*`) straight into
the copyparty volume — no HTTP upload needed.

**If you produced the dump off-box** (e.g. from a workstation with prod access),
upload it to copyparty with the admin password from the `COPYPARTY_ADMIN`
environment variable (copyparty accepts the password via the `PW:` header — do
not paste the literal value):

```bash
curl -T starwars-snapshot-YYYYMMDD.gz \
  -H "PW: $COPYPARTY_ADMIN" \
  https://copyparty.magaoidh.pro/<volume>/
```

Then update the `snapshot-url` AppHost parameter (and tell devs) to point at the
new file. Old snapshots can be deleted from copyparty once the new one is
verified.

### Recommended: schedule it (Unraid)

Rather than running it by hand, add `src/StarWarsData.SnapshotRestore/unraid-snapshot-cron.sh`
to the Unraid **User Scripts** plugin on a weekly schedule. It calls
`make-snapshot.sh`, publishes **atomically** to a stable filename
(`starwars-snapshot-latest.gz`) so a dev can never fetch a half-written
archive, keeps the last few dated copies for rollback, and is flock-guarded.
User Scripts run on the **Unraid host**, so use the host path. With copyparty
mapped `/w → /mnt/user/appdata/copyparty/files`, set in the editor (not in the
repo):

```bash
export MDB_URI='mongodb://…@localhost:27018/?authSource=admin&directConnection=true'
export COPYPARTY_VOL=/mnt/user/appdata/copyparty/files/swdata   # dedicated subfolder
```

That publishes to `…/files/swdata/starwars-snapshot-latest.gz`, served at
**`https://copyparty.magaoidh.pro/swdata/starwars-snapshot-latest.gz`**
(LAN: `http://192.168.1.102:3923/swdata/starwars-snapshot-latest.gz`). Set the
dev-side `snapshot-url` parameter to that **once** — it never changes.

Make sure the copyparty volume config (`/mnt/user/appdata/copyparty/config`)
grants read on `swdata` to whoever devs authenticate as (or anon-read; the URL
itself is the secret AppHost parameter). Because it runs on the box and writes
straight into the volume there is **no upload and `COPYPARTY_ADMIN` is not
needed**. Chosen over an AppHost resource on purpose — see
[Design-038](eng/design/038-developer-onboarding-snapshot.md) §"Automation decision".

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `snapshot-restore` says "nothing to do" | `snapshot-url` unset (Option A behaviour) — intended if on the shared server. |
| `snapshot-restore` skipped | Dev DB already has `raw.pages`. Drop the DB or set `FORCE=true` to re-restore. |
| Search/RAG returns nothing | Step 5 not run, or local Mongo is plain `mongo` not `mongodb-atlas-local`. |
| Chat errors / 401 | `Parameters:openapi` not set or invalid. |
| `snapshot-restore` refuses to run | Target DB name contains "prod" — the restore hard-guards against writing prod. |

For the snapshot mechanism's design and the maintainer's publish workflow, see
[eng/design/038-developer-onboarding-snapshot.md](eng/design/038-developer-onboarding-snapshot.md).
