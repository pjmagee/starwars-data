# Aspire isolated mode — usage guide for Claude Code

**Source:** [Aspire 13.2: Isolated mode for parallel development](https://devblogs.microsoft.com/aspire/aspire-isolated-mode-parallel-development/) · [`aspire run` reference](https://aspire.dev/reference/cli/aspire-run-command)
**Requires:** Aspire CLI **13.2+**
**Repo today:** [aspire.config.json](../../aspire.config.json) already pins `appHost.path` to [src/StarWarsData.AppHost/StarWarsData.AppHost.csproj](../../src/StarWarsData.AppHost/StarWarsData.AppHost.csproj), so every `aspire` command in this repo automatically targets the right AppHost without `--apphost`.

## TL;DR

When an agent (worktree, `/loop`, background) needs to start the AppHost while the user may already have one running, use:

```bash
aspire run --isolated --detach
```

Stop it again with `aspire ps` to find the instance ID, then `aspire stop <id>`.

That's the whole rule. The rest of this doc explains the *why* and the edge cases.

## What isolated mode actually does

`aspire run --isolated` (and `aspire start --isolated`) gives the AppHost instance:

1. **Randomised ports** for every resource endpoint and the dashboard, so two instances of the same AppHost don't fight over a fixed port.
2. **A separate user-secrets store**, keyed by the instance ID. Connection strings, API keys, and `Parameters:*` values written to the project's normal user-secrets store are **not** read by the isolated instance.

Both effects are the point of the feature: instances are sealed off from each other so the same `csproj` can run multiple times in parallel — exactly what happens when an agent in a worktree boots Aspire while the developer's main checkout is also running it.

## When Claude Code should use `--isolated`

Use it whenever there's a real or potential second AppHost on the machine:

- The agent was spawned with `isolation: "worktree"` and needs to start Aspire to validate a change.
- A `/loop` iteration or background `Bash(run_in_background: true)` boots the AppHost — the developer's `dotnet watch --project src/StarWarsData.AppHost` is almost certainly already running.
- Comparing two branches/configurations side-by-side (e.g. ETL phase timing on `main` vs. a feature branch).
- Running the smoke test of an `aspire publish`-ready change without disturbing the foreground dashboard on `https://localhost:17244`.

Skip `--isolated` only when the agent owns the machine outright (no foreground Aspire) **and** the run depends on the project's user-secrets (see "Gotchas" below).

## When *not* to use `--isolated`

The user-secrets isolation is the trap. The following commands **must not** be run with `--isolated`, because they resolve `Parameters:*` from the project's user-secrets store:

```bash
aspire publish    --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj
aspire do prepare-starwars -e Production --apphost ...
aspire deploy   -e Production            --apphost ...
```

(See [specs/017-aspire-publish-deploy-workflow/spec.md](../../specs/017-aspire-publish-deploy-workflow/spec.md) for the full parameter-resolution rules.) `--isolated` would point these commands at an empty secrets store and emit `.env.<env>` files full of unfilled values — which is exactly the failure mode that leaked secrets in the [feedback_ci_no_filled_env](../../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/feedback_ci_no_filled_env.md) incident, just from the other direction.

`--isolated` is for **`aspire run` / `aspire start`** only.

## Concrete commands for this repo

The CLAUDE.md run commands map to isolated equivalents like this:

| Foreground (developer)                                      | Agent / parallel (Claude Code)                                  |
| ----------------------------------------------------------- | --------------------------------------------------------------- |
| `dotnet run --project src/StarWarsData.AppHost`             | `aspire run --isolated --detach`                                |
| `dotnet watch --project src/StarWarsData.AppHost`           | *(don't — agents shouldn't watch; one-shot `run` + `stop`)*     |
| Inspect logs in the dashboard at the printed URL            | `aspire ps` → grab dashboard URL → use Aspire MCP against that  |

Useful flags to combine with `--isolated`:

- `--detach` — return the prompt and run the AppHost in the background. Pair with `aspire ps` / `aspire stop` so the agent can clean up.
- `--format Json` — only valid with `--detach`; emits the instance ID, dashboard URL, and ports as JSON for scripting.
- `--no-build` — skip restore/build when the agent has already built the solution in this turn.
- `--non-interactive` — disable spinners and prompts. Recommended for any agent invocation.

A typical agent flow:

```bash
# Build once with normal tools
dotnet build src/StarWarsData.slnx

# Boot an isolated AppHost in the background, capture its metadata
aspire run --isolated --detach --no-build --non-interactive --format Json > .aspire-instance.json

# ... do work, hit endpoints, scrape logs via Aspire MCP ...

# Tear down
aspire stop "$(jq -r '.instanceId' .aspire-instance.json)"
```

`aspire ps` is the recovery path if the instance ID is lost — it lists every running AppHost the CLI knows about, isolated or not.

## Interaction with the Aspire MCP

The `mcp__aspire__*` tools target whichever AppHost the CLI considers "current" via `aspire.config.json` and `aspire ps`. An isolated `--detach` instance shows up in `aspire ps` and is addressable through the MCP just like a foreground run — but **two isolated instances at once** means MCP calls need the instance ID disambiguator that `mcp__aspire__select_apphost` (and `list_apphosts`) provide. If only one Aspire instance is alive, no extra steps needed.

## Other shared state — isolation only fixes ports + user secrets

Booting the full AppHost (Admin + ApiService + Frontend + Mongo migrations + MCP sidecar) still touches a lot of shared infrastructure. Isolation does **not** sandbox any of this. Read this list before running an isolated AppHost from a background agent.

### Hangfire (the loudest risk)

The AppHost hardcodes `Settings__HangfireEnabled=true` for the Admin project at [src/StarWarsData.AppHost/Program.cs:73](../../src/StarWarsData.AppHost/Program.cs#L73), so an isolated Admin instance will:

1. **Start a second Hangfire server** against the shared `hangfire.*` collections in `starwars-dev`. The dev's Admin and the agent's Admin will race for queued jobs.
2. Run `RecurringJob.AddOrUpdate` for **7 jobs** at boot ([src/StarWarsData.Admin/Program.cs:205-224](../../src/StarWarsData.Admin/Program.cs#L205-L224)), **rewriting the cron expression** back to the AppHost defaults. Idempotent unless someone has hand-edited a cron — in which case the agent silently undoes it.
3. Execute any job whose cron actually fires during the agent's run window:

   | Job                       | Cron           | Default                      | Risk if it fires                                          |
   | ------------------------- | -------------- | ---------------------------- | --------------------------------------------------------- |
   | `daily-incremental-sync`  | `0 3 * * *`    | **ON**                       | hits Wookieepedia API                                     |
   | `daily-infobox-graph`     | `0 4 * * *`    | OFF                          | rebuilds `kg.*` if on                                     |
   | `daily-article-chunking`  | `0 5 * * *`    | **ON**                       | embedding generation = **billed**                         |
   | `refresh-ask-suggestions` | `0 3 * * 0`    | **ON**                       | LLM call = **billed**                                     |
   | `daily-holocron-pass`     | `0 6 * * *`    | gated by `HolocronEnabled`   | LLM workflow = **billed** + checkpoint pollution          |
   | `daily-openai-spend-sync` | `30 4 * * *`   | **ON**                       | read-only org billing                                     |

   The billed jobs (`daily-article-chunking`, `refresh-ask-suggestions`, `daily-holocron-pass`) are the dangerous ones for a long-running agent. Isolation does nothing to stop the agent's Admin from running them. The `JobToggleFilter` honours the shared `admin.JobToggle` collection, so OFF stays OFF.

### Other shared infrastructure the AppHost spins up

- **MongoDB** (external, self-hosted) — `starwars-dev` is shared between dev's run and the agent's run. Default to `starwars-dev` per [feedback_dev_db_first](../../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/feedback_dev_db_first.md). Never write to `starwars`.
- **MongoDbMigrations container** — runs at every AppHost start and writes to the `migrations` collection. Idempotent for already-applied migrations, but two simultaneous startups could race on a brand-new migration.
- **MongoDB MCP sidecar container** (`mongodb/mongodb-mcp-server`) — Aspire 13.2 isolation handles **ports** for this container; whether it auto-suffixes the **container name** is not documented in the blog post. Verify with `docker ps` after a first isolated run; if it collides with the foreground sidecar, isolation alone won't save you.
- **OpenAI API** — same `STARWARS_OPENAI_KEY`, same billing account. Any agent activity that triggers an LLM call costs the user real money.
- **Holocron checkpoints** in `genai.holocron_checkpoints` — agent Holocron runs pollute the same store; workflow-shape changes still invalidate per [feedback_workflow_shape_change_breaks_checkpoints](../../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/feedback_workflow_shape_change_breaks_checkpoints.md).
- **Wookieepedia MediaWiki API** — externally rate-limited; an agent's `daily-incremental-sync` firing alongside the dev's would burn the same shared quota.
- **Keycloak** — production at `auth.magaoidh.pro`. Frontend dev-auth bypass kicks in for `IsDevelopment()` per [feedback_dev_auth_bypass](../../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/feedback_dev_auth_bypass.md), so no real auth round-trip — safe.

### Other gotchas

- **User secrets are not shared.** An isolated run will not see `dotnet user-secrets set "Parameters:mongo-password" ...`. Pass anything the AppHost actually needs (Mongo creds, etc.) via env vars on the `aspire run` invocation instead.
- **Dashboard URL is random.** Don't hard-code `https://localhost:17244` in agent scripts. Read it from `aspire ps` or `--format Json`.
- **Don't `--isolated` the publish/deploy/prepare commands.** See above.

## Lighter-weight alternatives to running the whole AppHost

The cheapest way to handle all the shared-state risk is to **not boot the full AppHost from an agent** in the first place. Most agent jobs are "does the change build and respond" — for which the orchestrator is overkill.

- **Just verify the solution compiles** — `dotnet build src/StarWarsData.slnx`. No runtime needed.
- **Run unit tests** — `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"`. No Docker, no Mongo, no Aspire.
- **Run integration tests** — `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit|TestCategory=Integration"`. Testcontainers Mongo only, no AppHost.
- **Smoke-test an API endpoint change** — `dotnet run --project src/StarWarsData.ApiService` with `Settings__DatabaseName=starwars-dev` and `Settings__HangfireEnabled=false` set as env vars. No Admin, no Hangfire, no MCP sidecar, no migrations container, no Frontend.
- **Smoke-test an Admin/ETL change** — `aspire run --isolated --detach` is unavoidable here, but be aware Hangfire will boot. Disable any LLM-billed toggle (`daily-article-chunking`, `refresh-ask-suggestions`, `daily-holocron-pass`) in the shared `admin.JobToggle` collection before running.
- **Verify a Frontend Razor change renders** — `dotnet run --project src/StarWarsData.Frontend` with `services__apiservice__http__0` pointed at a running API (the dev's, or a separately-started one). No Admin, no Hangfire.

If you do need the full AppHost, the **Hangfire kill-switch** worth wiring is making `Settings__HangfireEnabled` a parameter (it's currently hardcoded `"true"` at [src/StarWarsData.AppHost/Program.cs:73](../../src/StarWarsData.AppHost/Program.cs#L73)) so an agent can disable Hangfire without code changes:

```csharp
var hangfireEnabled = builder.AddParameter("hangfire-enabled", value: "true");
// ...
.WithEnvironment("Settings__HangfireEnabled", hangfireEnabled);
```

Then `Parameters__hangfire-enabled=false aspire run --isolated --detach` would give the agent a Hangfire-free Admin without touching the dev's setup. Worth doing as a separate change if background-agent flows become routine.

## Recommended addition to `CLAUDE.md`

A short "Running Aspire from an agent" subsection under **Build & Run** would make this discoverable to future Claude Code sessions without having to read this doc. Suggested wording:

> When an agent (worktree, `/loop`, background) needs to boot the AppHost, use `aspire run --isolated --detach` instead of `dotnet run --project src/StarWarsData.AppHost`. The developer almost certainly has the AppHost running already; isolated mode (Aspire 13.2+) gives the agent its own randomized ports and a private user-secrets store, so the two instances don't collide. Tear down with `aspire stop <id>` (find the id with `aspire ps`).
>
> **But isolation only covers ports + secrets, not infrastructure.** The agent's Admin will start a second Hangfire server against the shared `hangfire.*` collections, rewrite the recurring-job cron expressions, and execute any job whose cron fires during the run. Prefer running a smaller subset (`dotnet run --project src/StarWarsData.ApiService` with `Settings__HangfireEnabled=false`) when the full AppHost isn't needed. Never use `--isolated` with `aspire publish`, `aspire do prepare-starwars`, or `aspire deploy` — those resolve `Parameters:*` from the project's user-secrets and an isolated store would silently produce empty `.env` files. Full guide: [eng/docs/aspire-isolated-mode-for-claude-code.md](eng/docs/aspire-isolated-mode-for-claude-code.md).
