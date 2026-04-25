# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

All projects live under `src/` with the solution at `src/StarWarsData.slnx`. Requires .NET 10 SDK (see `global.json` at the repo root).

```bash
# Build everything
dotnet build src/StarWarsData.slnx

# Run the Aspire orchestrator (starts API + Frontend)
dotnet run --project src/StarWarsData.AppHost

# Run just the API
dotnet run --project src/StarWarsData.ApiService

# Run just the frontend
dotnet run --project src/StarWarsData.Frontend

# Run just the admin app
dotnet run --project src/StarWarsData.Admin
```

### Publish & Deploy

The AppHost defines a Docker Compose environment named `starwars` (`AddDockerComposeEnvironment("starwars")` in [src/StarWarsData.AppHost/Program.cs](src/StarWarsData.AppHost/Program.cs)). Three CLI commands operate on it — pick by what you need:

```bash
# Template only — emits docker-compose.yaml + UNFILLED .env. Hand to CI.
aspire publish --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj

# Full bake — emits docker-compose.yaml + FILLED .env.<env> + container images.
# Resolves parameter values from user-secrets / Parameters__* env vars / appsettings.
aspire do prepare-starwars -e Production \
  --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj \
  --non-interactive

# Same as `prepare`, then runs `docker compose up` against the host.
aspire deploy -e Production --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj
```

Output lands in `src/StarWarsData.AppHost/aspire-output/` (gitignored). All parameter values for `prepare`/`deploy` resolve from the AppHost's user-secrets store — set them once with `dotnet user-secrets set "Parameters:<name>" <value> --project src/StarWarsData.AppHost`. **Do not** rely on shell env vars like `STARWARS_OPENAI_KEY` for AppHost parameters; those are runtime-only for the API service. See [eng/design/017-aspire-publish-deploy-workflow.md](eng/design/017-aspire-publish-deploy-workflow.md) for the full rationale, parameter resolution order, and the empty-string-user-secret gotcha.

### Aspire MCP

When the AppHost is running, use the **Aspire MCP server** (`mcp__aspire__*`) to interact with the running application:

- `list_resources` — see all running services and their status
- `list_console_logs` / `list_structured_logs` — read service logs and diagnose issues
- `list_traces` / `list_trace_structured_logs` — inspect distributed traces
- `execute_resource_command` — restart services or trigger the HTTP commands defined in the AppHost (ETL pipeline phases, index creation, etc.)
- `search_docs` / `list_docs` / `get_doc` — search and read .NET Aspire documentation directly

The AppHost defines HTTP commands on the **admin** resource for every ETL pipeline phase (1a–9). These are visible in the Aspire dashboard and executable via the MCP.

When working with Aspire APIs, configuration, or orchestration patterns, **always check the Aspire docs via the MCP first** (`mcp__aspire__search_docs`, `mcp__aspire__get_doc`) rather than guessing or relying on stale knowledge.

## Tests

The whole suite lives in **one project** — `src/StarWarsData.Tests` — and uses **MSTest on `MSTest.Sdk`** running through the new `Microsoft.Testing.Platform` (MTP) runner. The runner is selected via `global.json` at the repo root (`"test": { "runner": "Microsoft.Testing.Platform" }`); the legacy VSTest mode is no longer supported on .NET 10.

Tests are split into three tiers under matching folders, tagged with `[TestCategory(TestTiers.Unit|Integration|Agent)]`:

| Tier         | Folder                | What it touches                                | Where it runs                             |
| ------------ | --------------------- | ---------------------------------------------- | ----------------------------------------- |
| `Unit`       | `Unit/`               | Pure logic. No Docker, env vars, or network.   | Pre-commit + CI + Docker build            |
| `Integration`| `Integration/`        | Testcontainers MongoDB only.                   | CI (Docker required)                      |
| `Agent`      | `Agent/`              | Real OpenAI key + live `starwars-dev` MongoDB. | Manual / nightly only                     |

Filter syntax is the standard MSTest one. Always pass the test project with `--project` (the new MTP `dotnet test` mode requires it):

```bash
# Pre-commit / fast CI gate — pure unit tests, ~700ms
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"

# CI gate — unit + integration (needs Docker for Testcontainers), ~15s
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit|TestCategory=Integration"

# Manual / nightly — Agent tier (needs STARWARS_OPENAI_KEY + MDB_MCP_CONNECTION_STRING)
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Agent"

# Single test class
dotnet test --project src/StarWarsData.Tests --filter "FullyQualifiedName~RecordServiceTests"

# Single test method
dotnet test --project src/StarWarsData.Tests --filter "FullyQualifiedName~RecordServiceTests.GetCollectionNames_ResultsAreSorted"
```

**Fixtures** all live in `src/StarWarsData.Tests/Infrastructure/` and are **lazy static** — they expose `EnsureInitializedAsync()` and are wired in via `[ClassInitialize]` on each test class that needs them, with a single `[AssemblyCleanup]` in `AssemblyHooks.cs` disposing whichever ones got touched. Unit-only runs never spin up containers or contact OpenAI.

- `ApiFixture` — shared Testcontainers MongoDB seeded with diverse infobox types. Used by `RecordServiceTests`, `RelationshipAnalystToolkitTests`, `RelationshipGraphPipelineTests`.
- `CheckpointStoreFixture` — separate Testcontainers MongoDB for `MongoCheckpointStoreTests` (no seed).
- `AgentFixture` — real OpenAI client + live MongoDB connection for the Agent tier (`AskAiPipelineTests`, `DeepResearchTests`, `RelationshipQueryTests`, `TemporalQueryTests`).

`ArticleChunkingIntegrationTests` owns its own Testcontainer via `[ClassInitialize]`/`[ClassCleanup]` so chunking writes can't pollute the shared `ApiFixture` seed.

Parallelism: assembly-level `[Parallelize(Scope = ExecutionScope.ClassLevel)]` (sequential within a class, parallel across classes). Integration and Agent classes that share fixture state are marked `[DoNotParallelize]` to keep their writes from racing.

When adding a new test, decide its tier first and put it in the matching folder with `[TestCategory(TestTiers.X)]`. New tier constants live in `src/StarWarsData.Tests/TestTiers.cs`.

## Architecture

**Aspire-orchestrated app** with five runtime components:

- **AppHost** — .NET Aspire orchestrator. Wires MongoDB connection, OpenAI key, and defines HTTP commands for the ETL pipeline phases (visible in the Aspire dashboard). Also manages a MongoDB MCP sidecar container.
- **ApiService** — ASP.NET Core API. Hosts the AI agent (via Microsoft.Agents.AI + AGUI), MCP client for MongoDB tools, and feature endpoints. Organized into `Features/` folders: `CharacterTimelines`, `Chat`, `GalaxyMap`, `KnowledgeGraph`, `Pages`, `RPG`, `Search`, `Timeline`, `User`.
- **Admin** — Blazor Interactive Server app (MudBlazor UI) for ETL pipeline management, Hangfire background jobs, and admin dashboards. Organized into `Features/` folders: `Admin`, `KnowledgeGraph`, `Search`. Also has `Components/Pages/`, `Components/Layout/`, `Components/Shared/`.
- **Frontend** — Blazor Interactive Server with MudBlazor UI. Authenticates via Keycloak OIDC. Uses `Components/Pages/`, `Components/Layout/`, `Components/Shared/`.
- **MongoDbMigrations** — Run-once container (`mongo:latest`) that executes `mongosh migrate.js` against the target database. Tracked in a `migrations` collection — re-runs are idempotent. Scripts live in `src/StarWarsData.MongoDbMigrations/`.

**Shared libraries:**

- **Models** — Entities (`Page`, `Infobox`, `TimelineEvent`, `RelationshipEdge`, `CharacterTimeline`, `ArticleChunk`, etc.), DTOs, and `SettingsOptions` configuration.
- **Services** — All business logic organized by feature: `AI/Toolkits`, `Admin`, `CharacterTimelines`, `Chat`, `GalaxyMap`, `KnowledgeGraph`, `Pages`, `RPG`, `Search`, `Shared`, `Timeline`, `User`.
- **ServiceDefaults** — Aspire service defaults (OpenTelemetry, resilience).

**Folder convention:** ApiService, Admin, and Services use feature-based folder organization (`Features/<FeatureName>/` or `<FeatureName>/`), not layer-based (no `Controllers/`, `Repositories/` top-level folders).

## Key Patterns

**AI stack**: Microsoft.Extensions.AI (`IChatClient`) + Microsoft.Agents.AI (`AIAgent`, `AITool`) + OpenAI SDK. No Semantic Kernel — do not add SK packages.

**AI Agent pipeline** (in `ApiService/Program.cs`): Topic guardrail classifier -> AI agent with tool registry (ComponentToolkit, DataExplorerToolkit, GraphRAGToolkit, WikiSearchProvider, MongoDB MCP tools) -> AGUI streaming endpoint at `/kernel/stream`.

**MongoDB**: External self-hosted MongoDB Atlas Local (Community Edition) — not Aspire-managed. Connection string is assembled from parameters in the AppHost (`mongo-user`, `mongo-password`, `mongo-host`, `mongo-port`). **When using the MongoDB MCP server (`mcp__MongoDB__*`) directly, always connect using the host machine's `MDB_MCP_CONNECTION_STRING` environment variable** — do not hardcode credentials or construct connection strings manually. The default database is `starwars-dev` (safe for experimentation); production is `starwars` and must never be written to from dev tooling. Single database configured via `SettingsOptions.DatabaseName` with namespaced collections: `raw.*`, `timeline.*`, `kg.*`, `search.*`, `genai.*`, `chat.*`, `territory.*`, `galaxy.*`, `admin.*`, `hangfire.*`. Hangfire collections live in the same database, namespaced by the `Prefix` option (default `"hangfire"`). Collection names are defined in the `Collections` static class in `Settings.cs`. Production overrides via `appsettings.json` or env var `Settings__DatabaseName`.

**ETL pipeline** (ordered phases, triggered via admin endpoints or Aspire HTTP commands):

1. Download pages from Wookieepedia MediaWiki API
2. Create MongoDB views per infobox template type
3. Build categorized timeline events
4. Create indexes + embeddings + vector indexes
5. AI-generated character timelines
6. Relationship graph via OpenAI Batch API (submit/check/cleanup cycle)
7. Infer territory control from battle outcomes + government lifecycles

**Hangfire recurring jobs**: Daily incremental wiki sync (03:00 UTC), daily relationship graph builder (04:00 UTC), batch submissions every 30 min, batch status checks every 5 min, daily article chunking (05:00 UTC).

**Authentication**: Keycloak OIDC on the Frontend (users sign in at `auth.magaoidh.pro`). The API is internal-only (not exposed to the internet) — user identity is forwarded via `X-User-Id` header set by a `DelegatingHandler` from the authenticated `ClaimsPrincipal`. See `eng/adr/001-internal-api-auth.md` for the full rationale (JWT Bearer was attempted but is incompatible with Blazor Interactive Server mode).

**Admin section**: The **Admin** project is a separate Blazor app with its own admin endpoints and Hangfire dashboard. ETL pipeline phases are triggered via admin controllers (`Features/Admin/AdminController.cs`) and are also exposed as Aspire HTTP commands in the AppHost dashboard.

**GDPR compliance**: Cookie consent banner (blocks GA until accepted), Privacy Policy (`/privacy`), Terms of Use (`/terms`), "Delete All My Data" and "Export My Data" in Profile.

**Environment variables**: `STARWARS_OPENAI_KEY` for OpenAI API key, `MDB_MCP_CONNECTION_STRING` for the MongoDB MCP server connection.

## Code Conventions

- C# preview language features enabled (collection expressions, primary constructors, etc.)
- Nullable reference types enabled across all projects
- Configuration via `SettingsOptions` bound from `appsettings.json` section `"Settings"`
- Controllers under `ApiService/Features/<Feature>/` and `Admin/Features/<Feature>/` follow `[Route("api/[controller]")]` pattern
- Frontend pages under `Frontend/Components/Pages/`, shared components under `Frontend/Components/Shared/`

### Global Filter

The Frontend has a global filter bar (continuity: Canon/Legends, realm: Star Wars/Real) managed by `GlobalFilterService`. **Every page and component that queries the API must respect the global filter** by subscribing to `GlobalFilterService.OnChange` and passing the filter values via `GetContinuityQueryParam()` / `GetRealmQueryParam()` to API calls. When the filter changes, active queries and data must be refreshed.

### Continuity Color Convention

Continuity chips and badges **must** use MudBlazor theme colors consistently:

- `Continuity.Canon` → `Color.Primary`
- `Continuity.Legends` → `Color.Secondary`
- Everything else → `Color.Default`

Do not use `Color.Info`, `Color.Warning`, or other colors for continuity. This matches the `ContinuityFilter` toggle switches and `ContinuityBadge` component. See `ContinuityBadge.razor` and `ContinuityFilter.razor` as canonical references.

## Library Deviations

**Rule: standard library components are the default.** Third-party libraries in this repo (MudBlazor for UI, MudBlazor theming, MongoDB.Driver, Microsoft.Extensions.AI, Microsoft.Agents.AI, Hangfire, etc.) should be used via their public APIs. If the first instinct is to roll custom HTML/CSS, a custom abstraction, or a wrapper that bypasses the library's intended usage, **stop and reconsider** — the library almost always has a parameter, variant, or extension point that covers the case.

When a deviation is genuinely justified (the library's public API cannot meet the requirement), it **MUST** be recorded in an ADR under `eng/adr/` before or alongside the code change. The ADR entry must include:

1. The file/location of the deviation
2. The library component or API it replaces
3. The concrete reason the standard component cannot be used (with specifics — "too big" is not enough; give numbers, parameter names, and what was tried)
4. A **"Revisit when"** line describing the condition under which the deviation could be removed (e.g. a library feature being added)

The current catalogue of MudBlazor deviations lives in [eng/adr/004-mudblazor-deviations.md](eng/adr/004-mudblazor-deviations.md). When adding a new deviation for a component already covered by an existing ADR, add it to that ADR's **Catalogue** section rather than creating a new ADR. Create a new ADR only when deviating from a library/framework not yet covered.

Do not introduce silent deviations. A deviation that is not documented is a bug.

## MCP Servers & Skills

Use the attached MCP servers and skills for domain-specific guidance instead of guessing:

- **Aspire MCP** (`mcp__aspire__*`) — Interact with running Aspire resources: logs, traces, restart services, execute HTTP commands. Also provides `search_docs`/`get_doc` for looking up .NET Aspire documentation — use these before guessing at Aspire APIs.
- **MongoDB MCP** (`mcp__MongoDB__*`) — Query, aggregate, inspect schemas, manage indexes on the MongoDB databases. Always connects via the host `MDB_MCP_CONNECTION_STRING` env var. Default database: `starwars-dev` — never write to `starwars` (production).
- **Chrome DevTools MCP** (`mcp__chrome-devtools__*`) — Inspect live browser sessions: DOM snapshots, console logs, network requests, screenshots, performance traces, Lighthouse audits. Uses Brave browser.
- **MudBlazor MCP** (`mcp__mudblazor__*`) — Look up MudBlazor component docs, parameters, examples, and API reference when building or modifying Blazor UI.
- **Playwright MCP** (`mcp__playwright__*`) — Browser automation for testing and screenshots.
- **MediaWiki MCP** (`mcp__mediawiki-mcp-server__*`) — Search and fetch Wookieepedia pages directly.
- **OpenAI Developer Docs** (`mcp__openaiDeveloperDocs__*`) — Search OpenAI API docs and specs.

**Skills** (invoke with `/skill-name`). Installed and managed via `gh skill install <repo> <skill> --agent claude-code --scope project` (run `gh skill update --all` to pull upstream changes):

- `/claude-d3js-skill` — D3.js interactive visualization guidance.
- `/microsoft-agent-framework` — Microsoft Agent Framework (M.E.AI, Agents.AI) guidance for the AI pipeline.
- `/mongodb-natural-language-querying` — Generate MongoDB queries/aggregations from natural language.
- `/mongodb-query-optimizer` — MongoDB query performance and indexing advice.
- `/mongodb-schema-design` — MongoDB schema patterns and anti-patterns.
- `/mongodb-search-and-ai` — Atlas Search, Vector Search, and Hybrid Search guidance.
- `/dotnet-best-practices` — .NET/C# code quality and best practices review.
