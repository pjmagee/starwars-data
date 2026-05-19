# StarWarsData.SnapshotRestore

Developer-onboarding data snapshot — full design in
[eng/design/038-developer-onboarding-snapshot.md](../../eng/design/038-developer-onboarding-snapshot.md).
Onboarding walkthrough: [ONBOARDING.md](../../ONBOARDING.md).

Two halves:

| File | Who runs it | What it does |
|---|---|---|
| `make-snapshot.{sh,ps1}` | **Maintainer**, on a host with prod access | `mongodump` of `starwars-prod` → one `.gz`, excluding `chat.*`/`admin.*`/`hangfire.*`. Upload result to the snapshot host (object storage / static HTTPS — see [ONBOARDING.md](../../ONBOARDING.md)). |
| `Dockerfile` + `restore.sh` | **Aspire**, automatically | Run-once container (mirrors `mongodb-migrations`). Downloads the snapshot from `SNAPSHOT_URL` and `mongorestore`s it into the dev DB (`starwars-prod.*` → `<TARGET_DB>.*`). Idempotent; hard-refuses any DB named `*prod*`. |

The container is wired in `AppHost/Program.cs` only under
`IsDevelopment() && ExecutionContext.IsRunMode`, so it never enters
`aspire publish`/`prepare`/`deploy` output. `mongodb-migrations`
`WaitForCompletion`s it (restore's `--drop` must precede migrations).

After restore, recreate vector indexes via the Aspire `admin` commands
**Ensure All Indexes** → **4b. Create Index Embeddings** (keyless — embeddings
are already in the restored documents).

Not for recurring QA refresh — that is raw-only and lives in
[Design-033](../../eng/design/033-prod-to-dev-data-refresh.md).
