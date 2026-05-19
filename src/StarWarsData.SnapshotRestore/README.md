# StarWarsData.SnapshotRestore

Developer-onboarding data snapshot — full design in
[eng/design/038-developer-onboarding-snapshot.md](../../eng/design/038-developer-onboarding-snapshot.md).
Onboarding walkthrough: [ONBOARDING.md](../../ONBOARDING.md).

Two halves:

| File | Who runs it | What it does |
|---|---|---|
| `make-snapshot.{sh,ps1}` | **Maintainer**, on the Unraid box | `mongodump` of `starwars-prod` → one `.gz`, excluding `chat.*`/`admin.*`/`hangfire.*`. Writes straight into the copyparty volume (no upload). |
| `unraid-snapshot-cron.sh` | **Unraid User Scripts**, scheduled | Wraps `make-snapshot.sh`: atomic publish to a stable filename, dated-copy retention, flock-guarded. The recommended way to keep the snapshot fresh. |
| `Dockerfile` + `restore.sh` | **Aspire**, automatically | Run-once container (mirrors `mongodb-migrations`). Downloads the snapshot from `SNAPSHOT_URL` and `mongorestore`s it into the dev DB (`starwars-prod.*` → `<TARGET_DB>.*`). Idempotent, resumable, `gzip -t`-validated; hard-refuses any DB named `*prod*`. |

The container is wired in `AppHost/Program.cs` only under
`IsDevelopment() && ExecutionContext.IsRunMode`, so it never enters
`aspire publish`/`prepare`/`deploy` output. `mongodb-migrations`
`WaitForCompletion`s it (restore's `--drop` must precede migrations).

After restore, recreate vector indexes via the Aspire `admin` commands
**Ensure All Indexes** → **4b. Create Index Embeddings** (keyless — embeddings
are already in the restored documents).

Not for recurring QA refresh — that is raw-only and lives in
[Design-033](../../eng/design/033-prod-to-dev-data-refresh.md).
