# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

All projects live under `src/` with the solution at `src/StarWarsData.slnx`. Requires .NET 10 SDK (see `src/global.json`).

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

### Aspire MCP

When the AppHost is running, use the **Aspire MCP server** (`mcp__aspire__*`) to interact with the running application:

- `list_resources` — see all running services and their status
- `list_console_logs` / `list_structured_logs` — read service logs and diagnose issues
- `list_traces` / `list_trace_structured_logs` — inspect distributed traces
- `execute_resource_command` — restart services or trigger the HTTP commands defined in the AppHost (ETL pipeline phases, index creation, etc.)

The AppHost defines HTTP commands on the **admin** resource for every ETL pipeline phase (1a–8). These are visible in the Aspire dashboard and executable via the MCP.

## Tests

Tests use xUnit with Testcontainers for MongoDB (requires Docker running). There are no unit tests — all tests are integration tests that spin up real MongoDB containers.

```bash
# Run all tests
dotnet test src/StarWarsData.Tests

# Run a single test class
dotnet test src/StarWarsData.Tests --filter "FullyQualifiedName~RecordServiceTests"

# Run a single test method
dotnet test src/StarWarsData.Tests --filter "FullyQualifiedName~RecordServiceTests.Search_returns_matching_pages"
```

Test fixtures: `ApiFixture` (shared MongoDB container with seed data for most tests, xUnit collection `"Api"`) and `MongoFixture` (for relationship graph tests, collection `"Mongo"`). Both use `IAsyncLifetime` to start/stop Testcontainers.

## Architecture

**Aspire-orchestrated app** with four runtime components:

- **AppHost** — .NET Aspire orchestrator. Wires MongoDB connection, OpenAI key, and defines HTTP commands for the ETL pipeline phases (visible in the Aspire dashboard). Also manages a MongoDB MCP sidecar container.
- **ApiService** — ASP.NET Core API. Hosts the AI agent (via Microsoft.Agents.AI + AGUI), MCP client for MongoDB tools, and feature endpoints. Organized into `Features/` folders: `CharacterTimelines`, `Chat`, `GalaxyMap`, `KnowledgeGraph`, `Pages`, `RPG`, `Search`, `Timeline`, `User`.
- **Admin** — Blazor Interactive Server app (MudBlazor UI) for ETL pipeline management, Hangfire background jobs, and admin dashboards. Organized into `Features/` folders: `Admin`, `KnowledgeGraph`, `Search`. Also has `Components/Pages/`, `Components/Layout/`, `Components/Shared/`.
- **Frontend** — Blazor Interactive Server with MudBlazor UI. Authenticates via Keycloak OIDC. Uses `Components/Pages/`, `Components/Layout/`, `Components/Shared/`.

**Shared libraries:**

- **Models** — Entities (`Page`, `Infobox`, `TimelineEvent`, `RelationshipEdge`, `CharacterTimeline`, `ArticleChunk`, etc.), DTOs, and `SettingsOptions` configuration.
- **Services** — All business logic organized by feature: `AI/Toolkits`, `Admin`, `CharacterTimelines`, `Chat`, `GalaxyMap`, `KnowledgeGraph`, `Pages`, `RPG`, `Search`, `Shared`, `Timeline`, `User`.
- **ServiceDefaults** — Aspire service defaults (OpenTelemetry, resilience).

**Folder convention:** ApiService, Admin, and Services use feature-based folder organization (`Features/<FeatureName>/` or `<FeatureName>/`), not layer-based (no `Controllers/`, `Repositories/` top-level folders).

## Key Patterns

**AI stack**: Microsoft.Extensions.AI (`IChatClient`) + Microsoft.Agents.AI (`AIAgent`, `AITool`) + OpenAI SDK. No Semantic Kernel — do not add SK packages.

**AI Agent pipeline** (in `ApiService/Program.cs`): Topic guardrail classifier -> AI agent with tool registry (ComponentToolkit, DataExplorerToolkit, GraphRAGToolkit, WikiSearchProvider, MongoDB MCP tools) -> AGUI streaming endpoint at `/kernel/stream`.

**MongoDB**: External self-hosted server (not Aspire-managed). Connection string assembled from parameters in AppHost. Two databases configured via `SettingsOptions`: `starwars` (unified database with namespaced collections: `raw.*`, `timeline.*`, `kg.*`, `search.*`, `genai.*`, `chat.*`, `territory.*`, `galaxy.*`, `admin.*`) and `starwars-hangfire` (Hangfire job storage). Collection names are defined in the `Collections` static class in `Settings.cs`.

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

- **Aspire MCP** (`mcp__aspire__*`) — Interact with running Aspire resources: logs, traces, restart services, execute HTTP commands.
- **MongoDB MCP** (`mcp__MongoDB__*`) — Query, aggregate, inspect schemas, manage indexes on the MongoDB databases. Use `starwars-dev` for writes (see feedback memory).
- **MudBlazor MCP** (`mcp__mudmcp__*`) — Look up MudBlazor component docs, parameters, examples, and API reference when building or modifying Blazor UI.
- **Playwright MCP** (`mcp__playwright__*`) — Browser automation for testing and screenshots.
- **MediaWiki MCP** (`mcp__mediawiki-mcp-server__*`) — Search and fetch Wookieepedia pages directly.
- **OpenAI Developer Docs** (`mcp__openaiDeveloperDocs__*`) — Search OpenAI API docs and specs.

**Skills** (invoke with `/skill-name`):

- `/mudblazor` — MudBlazor component guidance for Blazor UI work.
- `/claude-d3js-skill` — D3.js interactive visualization guidance.
- `/microsoft-agent-framework` — Microsoft Agent Framework (M.E.AI, Agents.AI) guidance for the AI pipeline.
- `/mongodb-natural-language-querying` — Generate MongoDB queries/aggregations from natural language.
- `/mongodb-query-optimizer` — MongoDB query performance and indexing advice.
- `/mongodb-schema-design` — MongoDB schema patterns and anti-patterns.
- `/mongodb-search-and-ai` — Atlas Search, Vector Search, and Hybrid Search guidance.
- `/dotnet-best-practices` — .NET/C# code quality and best practices review.
