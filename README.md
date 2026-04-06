# Star Wars Data

A .NET Aspire application that processes Wookieepedia content into a temporal knowledge graph, powers AI-assisted exploration through semantic search and 50+ agent tools, and serves it all through an interactive Blazor frontend.

## Features

### AI Assistant

Conversational agent powered by OpenAI with tool-calling across six toolkits (50+ tools):

- **Charts** — Bar, Line, Pie, Donut, Stacked Bar, Radar, Rose, and Time Series
- **Relationship Graphs** — Family trees, political hierarchies, master-apprentice lineages (force + tree layout)
- **Timelines** — Temporal events scoped by era, date range, or entity lifetime
- **Data Tables** — Cross-referenced data assembled from multiple queries
- **Infobox Cards** — Wiki-style profiles with side-by-side comparison
- **Research Articles** — Formatted markdown with source citations from semantic search

All visualizations are grounded in real tool call results — no data is fabricated. Supports Bring Your Own Key (BYOK) for users who supply their own OpenAI API key. The Ask page features AI-generated dynamic example questions refreshed weekly from the knowledge graph.

### Semantic Search

AI-powered vector search over 800,000+ article passages using OpenAI embeddings and MongoDB Atlas Vector Search. Finds content by meaning, not keywords — ask "why did Anakin turn to the dark side?" and get answers from actual article text with deep-linked source references. Available in Ask AI and the Galaxy Map.

### Knowledge Graph

166,000+ entities and 694,000+ relationships extracted deterministically from Wookieepedia infobox data. Rich temporal facets track entity lifecycles across six semantic dimensions: lifespan, conflict, institutional, construction, creation, and publication. Two calendar systems (Galactic BBY/ABY and real-world CE).

### Graph Explorer

Interactive graph visualization with force-directed and hierarchical tree layouts. Browse entities, traverse relationships by label, expand nodes on double-click, navigate with breadcrumb history, and filter edges temporally with a year slider.

### Galaxy Map

Unified interactive galactic map with two modes:

- **Explore** — Drill from regions to systems to planets. Keyword and AI semantic search to locate places by concept (e.g., "planet where Anakin lost his legs" → Mustafar).
- **Timeline** — Animated playback of galactic events with territory control heatmap, era navigation, faction overlays, and event density visualization.

### Timeline Browser

Browse thousands of categorized events across all Star Wars eras with category filtering, year range scoping, and BBY/ABY navigation.

### Data Tables

Searchable, sortable, paginated tables for every infobox category: characters, ships, weapons, species, planets, governments, and more.

### Character Timelines

AI-generated biographical timelines synthesized from full wiki articles and linked pages.

## Architecture

```text
StarWarsData.AppHost          # .NET Aspire orchestrator
StarWarsData.ApiService       # ASP.NET Core API + AI agent
StarWarsData.Admin            # Admin dashboard + ETL job triggers + Hangfire
StarWarsData.Frontend         # Blazor Interactive Server UI
StarWarsData.Services         # Business logic, toolkits, ETL, search
StarWarsData.Models           # Shared entities and DTOs
StarWarsData.ServiceDefaults  # OpenTelemetry, health checks, resilience
StarWarsData.MongoDbMigrations # mongosh migration scripts (run-once container)
StarWarsData.Tests            # Integration tests (xUnit + Testcontainers)
StarWarsData.AgentTests       # AI agent evaluation tests (MSTest)
```

### Data Flow

Nine ordered ETL phases, triggered via the Aspire dashboard or Hangfire recurring jobs:

1. **Download Pages** — Fetch pages from Wookieepedia via the MediaWiki API, parse infobox data, store in `raw.pages`
2. **Template Views** — Create MongoDB views per infobox template type (Character, Planet, etc.)
3. **Timeline Events + Indexes** — Build categorized timeline events from the knowledge graph (dual-calendar: BBY/ABY + CE), create query indexes
4. **Article Chunking + Embeddings** — Split pages into semantic chunks, embed with OpenAI text-embedding-3-small (1536-dim), create vector search indexes
5. **Knowledge Graph** — Deterministic extraction builds `kg.nodes` + `kg.edges` from infobox links with temporal facets and edge weights
6. **Character Timelines** — AI-generated biographical timelines synthesized from full wiki articles
7. **LLM Relationship Graph** — Optional LLM-based relationship extraction via OpenAI Batch API (submit/check/cleanup cycle)
8. **Galaxy Map** — Pre-computes `galaxy.years` with territory control, event heatmaps, and trade routes from the knowledge graph
9. **Ask Suggestions** — AI agent explores the knowledge graph and generates Ask page example questions (weekly)

**Recurring Hangfire jobs:** Daily incremental wiki sync (03:00 UTC), daily relationship graph builder (04:00 UTC), batch submissions every 30 min, batch status checks every 5 min, daily article chunking (05:00 UTC), weekly Ask suggestions (Sundays 03:00 UTC).

### AI Agent Pipeline

Built with the Microsoft Agents AI framework, streaming via the AGUI protocol (SSE):

- **Topic Guardrail** — Lightweight classifier (GPT-4o-mini) rejects off-topic queries
- **Tool Registry** — Six composable toolkits:
  - **GraphRAGToolkit** (12 tools) — KG traversal + semantic search
  - **KGAnalyticsToolkit** (16 tools) — Aggregation queries for chart data
  - **DataExplorerToolkit** (10 tools) — Raw page and infobox queries
  - **ComponentToolkit** (8 tools) — Frontend render descriptors (charts, graphs, tables, cards)
  - **StarWarsWikiSearchProvider** (1 tool) — Keyword search fallback
  - **MongoDB MCP** (3 tools) — Direct find, aggregate, count via MCP sidecar
- **BYOK** — Users can supply their own OpenAI API key; BYOK users and admins are exempt from rate limiting
- **Semantic Search** — Shared SemanticSearchService used by the agent and galaxy map
- **References** — All render tools support source references with Wookieepedia URLs

### Authentication

Keycloak provides OpenID Connect authentication. Admin role bypasses rate limiting. User identity forwarded via X-User-Id/X-User-Roles headers from the Blazor frontend.

## Tech Stack

| Layer | Technology |
| ----- | ---------- |
| Orchestration | .NET Aspire 13.2 |
| Backend | ASP.NET Core, .NET 10 |
| Frontend | Blazor Interactive Server, MudBlazor, D3.js |
| Database | MongoDB (Atlas Community Edition), MongoDB.Driver 3.x |
| AI | OpenAI (GPT-5-mini, GPT-5, GPT-5.4-mini, text-embedding-3-small, Batch API), Microsoft.Extensions.AI, Microsoft.Agents.AI |
| Search | MongoDB Atlas Vector Search (semantic), MongoDB $text (keyword) |
| MCP | MongoDB MCP Server (sidecar), ModelContextProtocol client |
| Migrations | mongosh scripts in a run-once container, tracked in `migrations` collection |
| Background Jobs | Hangfire with MongoDB persistence |
| Auth | Keycloak (OpenID Connect) |
| Observability | OpenTelemetry (traces, metrics, logs) |

## MongoDB Collections

All data lives in a single unified database (default `starwars-dev` for local, `starwars` for production) with namespaced collections:

| Namespace | Collections | Purpose |
| --------- | ----------- | ------- |
| `raw.*` | `raw.pages`, `raw.job_state` | Downloaded Wookieepedia pages and ETL job state |
| `kg.*` | `kg.nodes`, `kg.edges`, `kg.labels`, `kg.crawl_state`, `kg.batch_jobs` | Knowledge graph entities, relationships, label registry, and LLM batch tracking |
| `search.*` | `search.chunks` | Article chunks with vector embeddings (1536-dim) |
| `timeline.*` | `timeline.{category}` | Categorized timeline events (one collection per infobox type) |
| `territory.*` | `territory.snapshots`, `territory.years` | Territory control snapshots and yearly data |
| `galaxy.*` | `galaxy.years` | Pre-computed galaxy map temporal data |
| `genai.*` | `genai.character_timelines`, `genai.character_checkpoints`, `genai.character_progress` | AI-generated character timelines and progress tracking |
| `chat.*` | `chat.sessions`, `chat.user_settings` | User chat history and BYOK API key settings |
| `admin.*` | `admin.job_toggles` | Feature toggles for admin jobs |
| `suggestions.*` | `suggestions.examples` | AI-generated Ask page example questions |

Hangfire uses a separate database (`starwars-dev-hangfire` or `starwars-hangfire`). MongoDB migrations are tracked in a `migrations` collection.

## Getting Started

### Prerequisites

- .NET 10 SDK
- Docker (for MongoDB MCP sidecar and Testcontainers)
- MongoDB instance (Atlas Community Edition or Atlas)
- OpenAI API key

### Configuration

Set the following in `appsettings.json` or environment variables:

```json
{
  "Settings": {
    "OpenAiKey": "<your-openai-api-key>",
    "OpenAiModel": "gpt-5-mini",
    "DatabaseName": "starwars-dev"
  }
}
```

The default database is `starwars-dev` for local development. Production overrides to `starwars` via `appsettings.json` or the `STARWARS_DB` environment variable.

### Running

```bash
cd src/StarWarsData.AppHost
dotnet run
```

The Aspire dashboard will show all resources. ETL pipeline phases are available as HTTP commands in the Aspire dashboard on the Admin resource.

### Post-Deployment

MongoDB migrations run automatically on startup via a run-once `mongo:latest` container that executes `mongosh migrate.js`. Migrations are tracked in the `migrations` collection and are idempotent.

After deploying, run **"Ensure All Indexes"** from the Aspire dashboard (Admin resource). This chains all index creation jobs: page indexes → chunk indexes → vector search index → KG graph indexes. The vector search index is required for semantic search to work.

## Support

This project is built and maintained as a solo effort. Significant time and cost go into running AI models during development and for the live application:

- **Claude** (Anthropic) — used extensively for development via Claude Code
- **OpenAI** (GPT-5-mini, GPT-4o-mini) — powers the in-app AI assistant, relationship extraction, and embeddings

If you find this project useful or interesting:

<a href="https://www.buymeacoffee.com/pjmagee"><img src="https://img.buymeacoffee.com/button-api/?text=Buy me a coffee&emoji=&slug=pjmagee&button_colour=FFDD00&font_colour=000000&font_family=Poppins&outline_colour=000000&coffee_colour=ffffff" /></a>
