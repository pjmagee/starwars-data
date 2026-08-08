# Maintainer: producing & publishing the onboarding snapshot

How the developer-onboarding data snapshot is created and published. This is a
**maintainer-only** operation — it needs the prod connection string, which never
ships in the repo or to devs (same rule as CI: a filled prod secret must never
leave the maintainer's machine — `feedback_ci_no_filled_env`).

For *why* the snapshot exists and the transport/automation decisions, see
[Design-038](../../specs/038-developer-onboarding-snapshot/spec.md). For the
*developer* (restore) side, see [ONBOARDING.md](../../ONBOARDING.md). This doc is
the operational how-to for the person making the snapshot.

## What a snapshot is

A single gzip archive produced by `mongodump` of **`starwars-prod`**, excluding
user/operational data:

- **Included:** `raw.*`, `kg.*`, `timeline.*`, `search.*` (incl. embedding
  vectors), `galaxy.*`, `genai.*` — everything needed to run the
  app without re-running ETL or spending OpenAI credit.
- **Excluded:** `chat.*` (user conversations), `admin.*`, `hangfire.*` — for
  privacy/GDPR and size.
- **Not captured:** Atlas Search / vector-search index *definitions*
  (`mongodump` cannot). Embeddings survive as document fields, so the dev
  recreates only the index definitions post-restore via the keyless Aspire
  commands — see ONBOARDING §5.

Current size: **~6.3 GB gzip** (from a ~29 GB DB; embeddings compress poorly).

## The two paths

| Aspect | On-box (preferred) | Off-box (workstation) |
| ------ | ------------------ | --------------------- |
| Where | Runs **on the Unraid box** (192.168.1.102) where prod Mongo + copyparty live | Maintainer workstation with prod network access |
| Mechanism | `unraid-snapshot-cron.sh` in the Unraid **User Scripts** plugin, weekly | `make-snapshot.{ps1,sh}` then manual upload |
| Upload | **None** — dump is written straight into the copyparty volume | `curl -T` over the **LAN** copyparty address |
| Secret needed | `MDB_URI` only (`COPYPARTY_ADMIN` not needed) | `MDB_URI` + `COPYPARTY_ADMIN` |
| Atomicity | Atomic publish to `starwars-snapshot-latest.gz` + retention | Manual: upload dated, then server-side move onto latest |

**Prefer the on-box cron.** The off-box path exists for ad-hoc refreshes when
you can't get onto the box; it is more steps and has the gotcha below.

## On-box (the normal path)

Add `src/StarWarsData.SnapshotRestore/unraid-snapshot-cron.sh` to the Unraid
**User Scripts** plugin on a weekly schedule. In the script editor (not the
repo) set:

```bash
export MDB_URI='mongodb://…@localhost:27018/?authSource=admin&directConnection=true'
export COPYPARTY_VOL=/mnt/user/appdata/copyparty/files/swdata
```

It dumps to a `*.partial` inside the volume, `gzip -t`-verifies, then
**atomically** renames onto `starwars-snapshot-latest.gz`, keeps the last 3
dated copies for rollback, and is `flock`-guarded. Because it writes straight
into the volume there is no upload and `COPYPARTY_ADMIN` is not needed. The
dev-side `snapshot-url` points at the stable file and **never changes**.

## Off-box (workstation, ad-hoc)

### 1. Prerequisites

- **MongoDB Database Tools** (`mongodump`). Not bundled with the repo:
  `winget install --id MongoDB.DatabaseTools -e` (Windows) — installs to
  `C:\Program Files\MongoDB\Tools\100\bin` (prepend to `PATH` for the session
  if a fresh terminal hasn't picked it up).
- **`MDB_URI`** = prod connection string. The scripts fall back to
  `MDB_MCP_CONNECTION_STRING` if `MDB_URI` is unset.
  - ⚠️ **Trim it.** A leading/trailing space makes `mongodump` fail with
    `error parsing uri: scheme must be "mongodb" or "mongodb+srv"`. Pass
    `$env:MDB_URI = $env:MDB_MCP_CONNECTION_STRING.Trim()`.
- **`COPYPARTY_ADMIN`** = copyparty admin password (for upload auth). Passed via
  the `PW:` header — never paste the literal value into a command line.

### 2. Dump

Write **outside the repo** — there is no `.gz`/snapshot rule in `.gitignore`, so
a dump in the repo root risks an accidental commit.

```powershell
$env:PATH = "C:\Program Files\MongoDB\Tools\100\bin;$env:PATH"
$env:MDB_URI = $env:MDB_MCP_CONNECTION_STRING.Trim()
$out = Join-Path $env:TEMP "starwars-snapshot-$(Get-Date -Format yyyyMMdd).gz"
src/StarWarsData.SnapshotRestore/make-snapshot.ps1 -Out $out
```

(bash twin: `make-snapshot.sh [output-path]` with `MDB_URI` exported.)

### 3. Upload — use the LAN address, not the public one

⚠️ **Cloudflare blocks the large *upload* only — not downloads.** The limit is
asymmetric and worth understanding:

- **Upload (request body):** `https://copyparty.magaoidh.pro` is proxied by
  Cloudflare, whose free-tier **request-body** cap (~100 MB) rejects a multi-GB
  `PUT`/`-T` with **`413 Payload Too Large`** (only the first ~2 MB transfer
  before CF kills it). So maintainers must upload via the LAN address.
- **Download (response body): *not* capped — devs are fine.** Verified against
  the published 6.3 GB file through the public URL: `HTTP 200` with the full
  `Content-Length` (6,620,767,690), `Accept-Ranges: bytes` preserved, and
  ranged `GET`s return `HTTP 206`, so `restore.sh`'s `curl -fL -C --retry 5`
  resume-on-drop works. `cf-cache-status: BYPASS` (the object is too big for
  free-tier CF caching) just means Cloudflare **streams it through** as a
  pass-through proxy rather than caching it — slower than LAN, but it works
  from anywhere. CF's ~100 s limit is time-to-**first**-byte, not total
  transfer, so a steadily-streaming multi-GB download does not 524 mid-flight
  (Design-038 transport caveats).

In short: **the 413 is an upload-side request-body limit; downloaders are
unaffected.** That asymmetry is why the dev-side default `snapshot-url` is the
public URL while maintainers must publish over the LAN.

Upload to the **LAN** copyparty address instead (bypasses Cloudflare); you must
be on the LAN/VPN to reach `192.168.1.102`:

```powershell
$src = Join-Path $env:TEMP "starwars-snapshot-$(Get-Date -Format yyyyMMdd).gz"
$pw  = $env:COPYPARTY_ADMIN.Trim()
curl.exe -sS -T "$src" -H "PW: $pw" `
  "http://192.168.1.102:3923/swdata/starwars-snapshot-$(Get-Date -Format yyyyMMdd).gz" `
  -w "HTTP %{http_code} | %{size_upload} bytes | %{time_total}s`n"
```

Upload as a **dated** filename first (never straight onto
`starwars-snapshot-latest.gz` — that file is what devs fetch, and a multi-minute
overwrite leaves a window where a clone gets a truncated archive).

### 4. Publish onto `latest` (server-side move — atomic)

copyparty's HTTP move API renames in place on the server, so devs see either
the old or the new file, never a partial. Method is **POST**, destination is an
absolute server path in `?move=`:

```powershell
$pw = $env:COPYPARTY_ADMIN.Trim()
curl.exe -sS -X POST -H "PW: $pw" `
  "http://192.168.1.102:3923/swdata/starwars-snapshot-$(Get-Date -Format yyyyMMdd).gz?move=/swdata/starwars-snapshot-latest.gz"
```

Because the dev-side `snapshot-url` already defaults to
`https://copyparty.magaoidh.pro/swdata/starwars-snapshot-latest.gz`, publishing
onto that name means **no AppHost parameter change and nothing to tell devs**.

### 5. Verify

```powershell
# Size on the server should match the local dump
curl.exe -sI -H "PW: $($env:COPYPARTY_ADMIN.Trim())" `
  "http://192.168.1.102:3923/swdata/starwars-snapshot-latest.gz" | Select-String -Pattern 'Content-Length'
```

A fresh-clone smoke test (drop `starwars-dev`, set `use-local-mongo=true`,
`aspire run`) is the real proof — the snapshot path is otherwise unproven until
exercised end-to-end (Design-038 "Validation status").

## Quick reference

| Symptom | Cause / fix |
| ------- | ----------- |
| `error parsing uri: scheme must be "mongodb"` | Leading/trailing space in `MDB_URI`/`MDB_MCP_CONNECTION_STRING` — `.Trim()` it. |
| `413 Payload Too Large` (Cloudflare) | Uploading the big file through the public URL. Use the LAN address `http://192.168.1.102:3923`. |
| `mongodump not found` | Install MongoDB Database Tools; prepend its `bin` to `PATH`. |
| Dev gets a truncated archive | Published non-atomically onto `latest`. Always upload dated, then server-side `?move=`. |
| Restore refuses to run | Target DB name contains "prod" — restore hard-guards against writing prod. |
