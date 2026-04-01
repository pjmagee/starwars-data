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
```

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

**Aspire-orchestrated app** with three runtime components:

- **AppHost** — .NET Aspire orchestrator. Wires MongoDB connection, OpenAI key, and defines HTTP commands for the ETL pipeline phases (visible in the Aspire dashboard).
- **ApiService** — ASP.NET Core API. Hosts controllers, Hangfire background jobs, an AI agent (via Microsoft.Agents.AI + AGUI), and an MCP client for MongoDB tools.
- **Frontend** — Blazor Interactive Server with MudBlazor UI. Authenticates via Keycloak OIDC.

**Shared libraries:**

- **Models** — Entities (`Page`, `Infobox`, `TimelineEvent`, `RelationshipEdge`, `CharacterTimeline`, `ArticleChunk`, etc.), DTOs, and `SettingsOptions` configuration.
- **Services** — All business logic: ETL services, AI toolkits, MongoDB operations.
- **ServiceDefaults** — Aspire service defaults (OpenTelemetry, resilience).

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

**Admin section**: Frontend pages under `/admin` are protected with `[Authorize(Roles = "admin")]`. The admin role is assigned in Keycloak. Admin pages provide buttons to trigger ETL pipeline phases, view graph builder/article chunks dashboards, and open the Hangfire dashboard. The API's admin endpoints are not separately authenticated — they rely on the Frontend's role check and network isolation.

**GDPR compliance**: Cookie consent banner (blocks GA until accepted), Privacy Policy (`/privacy`), Terms of Use (`/terms`), "Delete All My Data" and "Export My Data" in Profile.

**Environment variables**: `STARWARS_OPENAI_KEY` for OpenAI API key, `MDB_MCP_CONNECTION_STRING` for the MongoDB MCP server connection.

## Code Conventions

- C# preview language features enabled (collection expressions, primary constructors, etc.)
- Nullable reference types enabled across all projects
- Configuration via `SettingsOptions` bound from `appsettings.json` section `"Settings"`
- Controllers under `ApiService/Controllers/` follow `[Route("api/[controller]")]` pattern
- Frontend pages under `Frontend/Components/Pages/`, shared components under `Frontend/Components/Shared/`
