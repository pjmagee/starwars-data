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
  - Or skip the CLI entirely and use `dotnet run --project src/StarWarsData.AppHost`
- An **OpenAI API key** (yours — used only at runtime for chat/agent features)

## 2. MongoDB — pick one

The app talks to an external Mongo (it is **not** Aspire-managed). Two ways:

### Option A — you are on the LAN/VPN with the shared server

Nothing to do. `appsettings.Development.json` already points at the shared
server's `starwars-dev` database, which is kept current. Skip to step 3 and
**leave `snapshot-url` unset** — the restore container will no-op.

### Option B — fresh clone, no LAN access (the common case)

Run a local **Atlas-compatible** Mongo (plain `mongo` will NOT do — vector
search needs Atlas Local):

```bash
docker run -d --name starwars-mongo -p 27017:27017 \
  -e MONGODB_INITDB_ROOT_USERNAME=admin \
  -e MONGODB_INITDB_ROOT_PASSWORD=password \
  mongodb/mongodb-atlas-local:latest
```

Point the AppHost at it (user-secrets, AppHost project):

```bash
cd src/StarWarsData.AppHost
dotnet user-secrets set "Parameters:mongo-host" "localhost"
dotnet user-secrets set "Parameters:mongo-port" "27017"
dotnet user-secrets set "Parameters:mongo-user" "admin"
dotnet user-secrets set "Parameters:mongo-password" "password"
```

## 3. Secrets

From `src/StarWarsData.AppHost`:

```bash
# Your OpenAI key (required for chat/agent features)
dotnet user-secrets set "Parameters:openapi" "sk-..."

# Option B only: the data snapshot (see "Getting the snapshot URL" below)
dotnet user-secrets set "Parameters:snapshot-url" "<direct-download URL>"
```

Leave `snapshot-url` unset for Option A.

### Getting the snapshot URL

Snapshots are hosted on the project's **copyparty** file server. `snapshot-url`
is the **direct file URL** (not a folder/listing — the restore `gzip -t`-checks
the download and fails fast if it gets HTML):

- **On LAN/VPN (fastest, recommended):** `http://192.168.1.102:3923/<volume>/starwars-snapshot-YYYYMMDD.gz`
  — bypasses Cloudflare entirely, full local speed, no proxy timeout.
- **Remote:** `https://copyparty.magaoidh.pro/<volume>/starwars-snapshot-YYYYMMDD.gz`
  — goes through Cloudflare. A ~15 GB pull can hit CF's ~100 s proxy timeout
  on a slow link; the restore retries with resume (`curl -C -`), but prefer
  the LAN URL when you can.
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

On first run (Option B), the Aspire dashboard shows a **`snapshot-restore`**
resource that:

1. downloads the snapshot,
2. restores it into your `starwars-dev` database (renaming `starwars-prod.*` →
   `starwars-dev.*`),
3. exits.

`mongodb-migrations` waits for it to finish, then applies schema/index
migrations. It is **idempotent** — restart the AppHost any time; the restore
skips itself once the dev DB is populated (delete the dev DB or set
`Parameters:snapshot-restore` env `FORCE=true` to redo it).

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
Set `MDB_URI` and `COPYPARTY_VOL` in the User Scripts editor (not in the repo).
Because it runs on the box and writes straight into the copyparty volume there
is **no upload and `COPYPARTY_ADMIN` is not needed**, and `snapshot-url` is set
once (to the stable file) and never changes. This was chosen over an AppHost
resource on purpose — see [Design-038](eng/design/038-developer-onboarding-snapshot.md)
§"Automation decision".

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
