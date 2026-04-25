# Design-017: Aspire Publish & Deploy Workflow

**Status:** Accepted
**Date:** 2026-04-25
**Author:** Patrick Magee + Claude
**Related:** [ADR-005 MongoDB Migration Strategy](../adr/005-mongodb-migration-strategy.md), [Design-010 MongoDB Migration Environment Promotion](./010-mongodb-migration-environment-promotion.md), [CLAUDE.md (Build & Run section)](../../CLAUDE.md)

## Problem

The Aspire AppHost orchestrates the four runtime apps (Frontend, Admin, ApiService, MongoDbMigrations) plus the MongoDB MCP sidecar. Going from a working `dotnet run --project src/StarWarsData.AppHost` to a deployable Docker Compose bundle (`docker-compose.yaml` + filled-in `.env`) was unclear: parameter values lived in a mix of OS environment variables (`STARWARS_OPENAI_KEY`, `KEYCLOAK_ADMIN_SECRET`) and `dotnet user-secrets` (mongo creds), which forced the operator to remember which secret lived where, and to set the env vars in their shell before any publish/deploy command would resolve them.

Compounding that, the three Aspire CLI commands in this space — `aspire publish`, `aspire do prepare-<env>`, and `aspire deploy` — *look* similar but produce very different artifacts, and only one of them actually fills the `.env` from local secrets.

## Aspire publish lifecycle

Three commands operate on the Docker Compose environment resource (`AddDockerComposeEnvironment("starwars")` in [src/StarWarsData.AppHost/Program.cs](../../src/StarWarsData.AppHost/Program.cs)):

| Command | Output | Parameter resolution | Builds images? | Deploys? |
| --- | --- | --- | --- | --- |
| `aspire publish` | `docker-compose.yaml` + **unfilled** `.env` template | None — placeholders only | No | No |
| `aspire do prepare-starwars -e <env>` | `docker-compose.yaml` + **filled** `.env.<env>` + container images | Full chain (env vars → user-secrets → appsettings → prompt) | Yes | No |
| `aspire deploy -e <env>` | Same as `prepare`, then `docker compose up` | Full chain | Yes | Yes |

`publish` is the "hand the artifact to CI" path — secrets get injected later by the pipeline. `prepare` is the "I want a fully-baked bundle locally" path. `deploy` is `prepare` plus `docker compose up`.

All three respect the `-e | --environment <name>` flag, which sets `DOTNET_ENVIRONMENT` for the AppHost run, drives `appsettings.{Environment}.json` selection, and names the output env file (`.env.Production`, `.env.Staging`, etc.).

## Parameter resolution order

For any `builder.AddParameter("name")` call, Aspire resolves the value at publish/prepare/deploy time in this order:

1. **`AddParameter("name", value: "...")` inline** — if `value:` is supplied, it **wins** over everything else. It is treated as the *resolved* value, not a fallback. Don't use `value:` if you want config sources to drive the parameter.
2. **Environment variable** `Parameters__<name>` (double underscore separator, dashes in the parameter name become single underscores: `mongo-password` → `Parameters__mongo_password`).
3. **AppHost configuration** — `appsettings.json`, `appsettings.{Environment}.json`, **user-secrets** (the AppHost project's own user-secrets store, *but see caveat below*), or any other `IConfiguration` source under the `Parameters:<name>` key.
4. **Interactive prompt** — Aspire dashboard at run time, or CLI prompt at publish/prepare/deploy time. Fails with a hard error in `--non-interactive` mode.

**Empty string is not "having a value."** A user-secret or env var of `""` does *not* satisfy `AddParameter("name", secret: true)` — Aspire treats it as missing and the prepare step fails with `Parameter resource could not be used because configuration key 'Parameters:<name>' is missing and the Parameter has no default value`. If the parameter genuinely should default to empty, use `AddParameter("name", value: "", secret: true)` so the inline default kicks in.

**User-secrets are loaded only in `Development` by default.** Standard .NET behavior: `AddUserSecrets()` registers itself only when `DOTNET_ENVIRONMENT=Development`. The AppHost's [Program.cs](../../src/StarWarsData.AppHost/Program.cs) explicitly calls `builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true)` so secrets resolve in *all* environments — including `aspire do prepare-starwars -e Production` runs from a developer machine. Without this line, local Production prepares fail unless every secret is supplied via `Parameters__*` env vars.

**Deployment state cache.** Aspire writes resolved parameter values to `~/.aspire/deployments/<hash>/<environment>.json` after every successful prepare/publish/deploy. **The cache is read on subsequent runs and overrides current config sources** — meaning if you change a parameter's source (move it from `value:` to user-secrets, or change an `appsettings.Production.json` value), the cached value sticks until you delete the file. When debugging "why is this still resolving to the old value," check `~/.aspire/deployments/` first.

## Decisions

### Use user-secrets for *all* parameter values, not OS env vars

The AppHost project's user-secrets store (`dotnet user-secrets set "Parameters:<name>" <value> --project src/StarWarsData.AppHost`) owns every secret needed at publish/prepare/deploy time:

- `Parameters:openapi` — OpenAI API key (was previously read from `STARWARS_OPENAI_KEY` env var)
- `Parameters:mongo-user`, `Parameters:mongo-password`, `Parameters:mongo-host`, `Parameters:mongo-port`

`STARWARS_OPENAI_KEY` is still consumed at runtime by the API service for the live agent loop, but the AppHost no longer reads it. This means `aspire do prepare-starwars -e Production` works with no env-var dance — the operator only has to manage one secret store.

### `keycloak-admin-secret` keeps an inline empty default

`AddParameter("keycloak-admin-secret", value: "", secret: true)` — the parameter exists in the model so the AppHost dependency wiring stays valid, but the *real* value is injected into the running container at `docker compose up` time via the host's `KEYCLOAK_ADMIN_SECRET` env var. This is wired through `ConfigureComposeFile` which writes `Settings__KeycloakAdminClientSecret = "${KEYCLOAK_ADMIN_SECRET:-}"` directly into the apiservice service definition.

This split keeps the secret out of the deployable artifact (`.env.Production`) entirely — only the deploy host ever sees it.

### Output: `aspire-output/` is gitignored, treated as ephemeral

Both `.env` (template) and `.env.<env>` (filled, contains real secrets) live in `src/StarWarsData.AppHost/aspire-output/`, covered by the `aspire-output/` rule in [.gitignore](../../.gitignore). Regenerate freely; never commit; never edit by hand.

## Operator workflow

```bash
# One-time setup — populate the AppHost user-secrets store
dotnet user-secrets set "Parameters:openapi"        "sk-proj-..."  --project src/StarWarsData.AppHost
dotnet user-secrets set "Parameters:mongo-user"     "admin"        --project src/StarWarsData.AppHost
dotnet user-secrets set "Parameters:mongo-password" "..."          --project src/StarWarsData.AppHost
dotnet user-secrets set "Parameters:mongo-host"     "192.168.1.x"  --project src/StarWarsData.AppHost
dotnet user-secrets set "Parameters:mongo-port"     "27018"        --project src/StarWarsData.AppHost

# Build images + write a filled .env.Production for the prod deploy host
aspire do prepare-starwars -e Production \
  --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj \
  --non-interactive

# Inspect the artifact
ls src/StarWarsData.AppHost/aspire-output/
# → docker-compose.yaml, .env (template), .env.Production (filled)
```

On the deploy host, set `KEYCLOAK_ADMIN_SECRET` in the shell, then `docker compose --env-file .env.Production up -d`.

## Aspire CLI version notes

This workflow targets **Aspire 13.2** (current as of 2026-04-25). The `aspire publish` / `aspire do prepare-<env>` / `aspire deploy` commands are all marked **Preview** in the CLI help. Behavior may shift in future versions — always check `aspire <command> --help` before relying on a flag, and use `mcp__aspire__search_docs` / `get_doc` to verify against current Aspire docs.

## CI publishing pipeline

[.github/workflows/deploy.yml](../../.github/workflows/deploy.yml) (the `Build & Publish` workflow) runs on every push to `main`:

1. Builds the solution + runs the unit-test tier.
2. `aspire do push` — builds and pushes container images to `ghcr.io/pjmagee/starwars-data` (versioned via GitVersion semVer + a `latest` tag).
3. `aspire publish -e Production` — emits `docker-compose.yaml` + an **unfilled** `.env.Production` template into `./aspire-output/`. **No secrets are passed to this step**, by design: the public-repo artifact is downloadable by any logged-in GitHub user, so the workflow must never produce a filled env file. A subsequent grep guard fails the build if the artifact ever contains text matching an OpenAI key or a populated `Parameters__mongo_password=…`, in case Aspire's `publish` contract changes.
4. Uploads the `release` artifact (compose + template) and pushes a `vX.Y.Z` git tag.

The deploy host is responsible for materializing real values into `.env.Production` from its own secret store before `docker compose --env-file .env.Production up -d`.

### History — never use `prepare-starwars` in CI

The workflow originally ran `aspire do prepare-starwars -e Production` with `Parameters__openapi`, `Parameters__mongo_user`, and `Parameters__mongo_password` injected from GitHub Actions secrets. This produced a **filled** `.env.Production` (containing the live OpenAI key and Mongo credentials) and uploaded it as the `release` artifact. Because the repository is public and Actions artifacts inherit the workflow run's audience, those credentials were downloadable by any authenticated GitHub user for the artifact's 30-day retention window. The secrets were rotated; the workflow now uses `publish` (template only). Do not reintroduce `prepare-starwars` or `deploy` in any CI step that produces an uploaded artifact.

## Out of scope / not done

- **`aspire deploy` end-to-end.** Tested only through `prepare-starwars` locally. The full `deploy` path that calls `docker compose up` against the deploy host hasn't been exercised from this machine.
- **Per-environment user-secrets.** All operators today share one user-secrets store on the dev machine. If multiple environments need different secrets locally, the right pattern is per-environment `appsettings.{Environment}.json` files in the AppHost project (which Aspire respects via `-e <env>`), not multiple user-secret stores.
