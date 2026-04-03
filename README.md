# Star Wars Data

A .NET Aspire application that processes Wookieepedia content into a temporal knowledge graph, powers AI-assisted exploration through semantic search and 30+ agent tools, and serves it all through an interactive Blazor frontend.

## Features

### AI Assistant

Conversational agent powered by OpenAI with tool-calling across four toolkits:

- **Charts** — Bar, Line, Pie, Donut, Stacked Bar, Radar, Rose, and Time Series
- **Relationship Graphs** — Family trees, political hierarchies, master-apprentice lineages (force + tree layout)
- **Timelines** — Temporal events scoped by era, date range, or entity lifetime
- **Data Tables** — Cross-referenced data assembled from multiple queries
- **Infobox Cards** — Wiki-style profiles with side-by-side comparison
- **Research Articles** — Formatted markdown with source citations from semantic search

All visualizations are grounded in real tool call results — no data is fabricated.

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
StarWarsData.ApiService       # ASP.NET Core API + AI agent + Hangfire
StarWarsData.Admin            # Admin dashboard + ETL job triggers
StarWarsData.Frontend         # Blazor Interactive Server UI
StarWarsData.Services         # Business logic, toolkits, ETL, search
StarWarsData.Models           # Shared entities and DTOs
StarWarsData.ServiceDefaults  # OpenTelemetry, health checks, resilience
StarWarsData.Tests            # Integration tests (xUnit + Testcontainers)
StarWarsData.AgentTests       # AI agent evaluation tests (MSTest)
```

### Data Flow

1. **ETL Pipeline** — Hangfire jobs download pages from Wookieepedia via the MediaWiki API, parse infobox data, and store raw pages in MongoDB
2. **Knowledge Graph** — Deterministic extraction builds kg.nodes + kg.edges from infobox links with temporal facets and edge weights
3. **Article Chunking** — Pages are split into semantic chunks and embedded with OpenAI text-embedding-3-small (1536 dimensions)
4. **Galaxy Map** — Pre-computes galaxy.years with territory control, event heatmaps, and trade routes from the knowledge graph
5. **Daily Sync** — Recurring job at 03:00 UTC incrementally syncs changed wiki pages
6. **AI Queries** — Agent uses tool-calling to query the KG, run semantic search, and render results via the frontend

### AI Agent Pipeline

Built with the Microsoft Agents AI framework, streaming via the AGUI protocol (SSE):

- **Topic Guardrail** — Lightweight classifier rejects off-topic queries
- **Tool Registry** — GraphRAGToolkit (KG traversal + semantic search), DataExplorerToolkit (page queries), ComponentToolkit (render tools), WikiSearchProvider (keyword fallback), MongoDB MCP tools
- **Semantic Search** — Shared SemanticSearchService used by the agent and galaxy map
- **References** — All render tools support source references with Wookieepedia URLs

### Authentication

Keycloak provides OpenID Connect authentication. Admin role bypasses rate limiting. User identity forwarded via X-User-Id/X-User-Roles headers from the Blazor frontend.

## Tech Stack

| Layer | Technology |
| ----- | ---------- |
| Orchestration | .NET Aspire |
| Backend | ASP.NET Core, .NET 10 |
| Frontend | Blazor Interactive Server, MudBlazor, D3.js |
| Database | MongoDB (Atlas Community Edition) |
| AI | OpenAI (GPT, Embeddings, Batch API), Microsoft.Extensions.AI, Microsoft.Agents.AI |
| Search | MongoDB Atlas Vector Search (semantic), MongoDB $text (keyword) |
| MCP | MongoDB MCP Server |
| Background Jobs | Hangfire with MongoDB persistence |
| Auth | Keycloak (OpenID Connect) |
| Observability | OpenTelemetry (traces, metrics, logs) |

## MongoDB Collections

All data lives in a single unified database (`starwars`) with namespaced collections:

| Namespace | Collections | Purpose |
| --------- | ----------- | ------- |
| `raw.*` | `raw.pages` | Downloaded Wookieepedia pages with infobox data |
| `kg.*` | `kg.nodes`, `kg.edges` | Knowledge graph entities and relationships |
| `search.*` | `search.chunks` | Article chunks with vector embeddings (1536-dim) |
| `timeline.*` | `timeline.{category}` | Categorized timeline events |
| `galaxy.*` | `galaxy.years` | Pre-computed galaxy map temporal data |
| `genai.*` | `genai.character_timelines` | AI-generated character timelines |
| `chat.*` | `chat.sessions` | User chat session history |
| `admin.*` | `admin.*` | Job toggles, user settings |

Hangfire uses a separate `starwars-hangfire` database.

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
    "DatabaseName": "starwars",
    "HangfireDb": "starwars-hangfire"
  }
}
```

### Running

```bash
cd src/StarWarsData.AppHost
dotnet run
```

The Aspire dashboard will show all resources. ETL pipeline phases are available as HTTP commands in the Aspire dashboard on the Admin resource.

### Post-Deployment

After deploying, run **"Ensure All Indexes"** from the Aspire dashboard (Admin resource). This chains all index creation jobs: page indexes → chunk indexes → vector search index → KG graph indexes. The vector search index is required for semantic search to work.

## Support

This project is built and maintained as a solo effort. Significant time and cost go into running AI models during development and for the live application:

- **Claude** (Anthropic) — used extensively for development via Claude Code
- **OpenAI** (GPT-5-mini, GPT-4o-mini) — powers the in-app AI assistant, relationship extraction, and embeddings

If you find this project useful or interesting:

<a href="https://www.buymeacoffee.com/pjmagee"><img src="https://img.buymeacoffee.com/button-api/?text=Buy me a coffee&emoji=&slug=pjmagee&button_colour=FFDD00&font_colour=000000&font_family=Poppins&outline_colour=000000&coffee_colour=ffffff" /></a>
