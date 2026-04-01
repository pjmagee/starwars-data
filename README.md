# Star Wars Data

A .NET Aspire application that scrapes Wookieepedia, processes Star Wars universe data into MongoDB, and serves it through an AI-powered Blazor frontend with interactive visualizations.

## Features

### AI Assistant (Ask Page)

An AI chat interface powered by OpenAI with tool-calling capabilities. The agent can query the database and render results as:

- **Charts** - Bar, Line, Pie, Donut, Stacked Bar, Radar, and Time Series
- **Tables** - Paginated browsing by infobox type, or ad-hoc data tables with inline data
- **Relationship Graphs** - Family trees and master/apprentice hierarchies
- **Timelines** - Temporal events scoped by era, date range, or entity lifetime
- **Infobox Cards** - Wiki-style info cards for specific entities
- **Text Summaries** - Article excerpts and lore via wiki search RAG

All visualizations include source references linking back to Wookieepedia.

### Galactic Map

Interactive 26x20 grid map of the Star Wars galaxy featuring:

- Color-coded regions with filtering
- Drill-down into sectors, systems, planets, and nebulas
- Detail panes for selected entities

### Timeline Browser

Browse events across the Star Wars timeline with category filtering, year range scoping, and pagination.

### Data Tables

Browse all infobox categories (Characters, Planets, Species, Starships, etc.) as paginated tables with dynamic columns.

### Wiki Search (RAG)

On-demand text search against the full wiki page corpus using MongoDB regex matching. The AI agent uses this to answer lore and history questions with cited sources.

## Architecture

```text
StarWarsData.AppHost          # .NET Aspire orchestrator
StarWarsData.ApiService       # ASP.NET Core API + AI agent + Hangfire
StarWarsData.Frontend         # Blazor Interactive Server UI
StarWarsData.Services         # Business logic, toolkits, ETL
StarWarsData.Models           # Shared entities and DTOs
StarWarsData.ServiceDefaults  # OpenTelemetry, health checks, resilience
```

### Data Flow

1. **ETL Pipeline** - Hangfire jobs scrape Wookieepedia pages via the MediaWiki API, parse infoboxes, and store raw pages in MongoDB
2. **Processing** - Additional jobs create template-based views, timeline events, indexes, and OpenAI embeddings
3. **Daily Sync** - A recurring job at 03:00 UTC incrementally syncs changed wiki pages
4. **AI Queries** - The AI agent uses tool-calling to query MongoDB (via direct tools + MCP server) and renders results through the frontend

### AI Agent Pipeline

The agent is built with the Microsoft Agents AI framework and streams responses via the AGUI protocol (SSE):

- **Topic Guardrail** - Lightweight classifier rejects off-topic queries
- **Tool Registry** - ComponentToolkit (7 render tools), DataExplorerToolkit (search/query tools), WikiSearchProvider (RAG), and MongoDB MCP tools (find, aggregate, count)
- **References** - All render tools support source references with Wookieepedia URLs

### Authentication

Keycloak provides OpenID Connect authentication with optional social login providers:

- Google, Microsoft, Facebook (requires client ID/secret configuration)
- Chat session history is stored per-user in MongoDB

## Tech Stack

| Layer | Technology |
| ----- | ---------- |
| Orchestration | .NET Aspire |
| Backend | ASP.NET Core, .NET 10 |
| Frontend | Blazor Interactive Server, MudBlazor |
| Database | MongoDB |
| AI | OpenAI (GPT, Embeddings), Microsoft.Agents.AI, AGUI |
| MCP | MongoDB MCP Server (`@mongodb-js/mongodb-mcp-server`) |
| Background Jobs | Hangfire with MongoDB persistence |
| Auth | Keycloak (OpenID Connect) |
| Diagrams | Z.Blazor.Diagrams |
| Observability | OpenTelemetry (traces, metrics, logs) |
| Analytics | Google Analytics 4 |

## MongoDB Databases

All application data lives in a single unified database with namespaced collections:

| Database | Purpose |
| -------- | ------- |
| `starwars` | All application data — raw pages (`raw.*`), timeline events (`timeline.*`), knowledge graph (`kg.*`), article chunks (`search.*`), AI-generated content (`genai.*`), chat sessions (`chat.*`), territory control (`territory.*`), galaxy map (`galaxy.*`), admin settings (`admin.*`) |
| `starwars-hangfire` | Hangfire job/queue storage |

## Getting Started

### Prerequisites

- .NET 10 SDK
- Node.js (for MongoDB MCP server via npx)
- MongoDB instance
- OpenAI API key

### Configuration

Set the following in `appsettings.json` or environment variables:

```json
{
  "Settings": {
    "OpenAiKey": "<your-openai-api-key>",
    "OpenAiModel": "gpt-5-mini",
    "StarWarsBaseUrl": "https://starwars.fandom.com/api.php",
    "DatabaseName": "starwars",
    "HangfireDb": "starwars-hangfire"
  }
}
```

For Keycloak social login, set environment variables:

```bash
GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET
MICROSOFT_CLIENT_ID / MICROSOFT_CLIENT_SECRET
FACEBOOK_CLIENT_ID / FACEBOOK_CLIENT_SECRET
```

### Running

```bash
cd src/StarWarsData.AppHost
dotnet run
```

The Aspire dashboard will show all resources. The Hangfire dashboard is available at `/hangfire` on the API service.

## API Endpoints

| Controller | Path | Purpose |
| ---------- | ---- | ------- |
| Admin | `/api/admin/*` | ETL job triggers, index management |
| Pages | `/pages/{id}`, `/pages/batch` | Page retrieval |
| Categories | `/categories/{type}` | Paginated infobox browsing |
| Search | `/search` | Full-text page search |
| Timeline | `/timeline/events`, `/timeline/eras` | Timeline data |
| GalaxyMap | `/galaxymap/grid`, `/galaxymap/sectors` | Galaxy map data |
| Relationships | `/relationships/graph/{id}` | Relationship graphs |
| ChatSessions | `/api/ChatSessions` | User chat history |
| AI (AGUI) | `/kernel/stream` | AI agent SSE streaming |

## Support

This project is built and maintained as a solo effort. Significant time and cost go into running AI models during development and for the live application:

- **Claude** (Anthropic) — used extensively for development via Claude Code, including architecture, code generation, debugging, and test authoring
- **OpenAI** (GPT-4o, GPT-4o-mini, GPT-5-mini) — powers the in-app AI assistant, relationship graph extraction agent, and embedding generation

If you find this project useful or interesting, consider buying me a coffee to help offset the API costs:

<a href="https://www.buymeacoffee.com/pjmagee"><img src="https://img.buymeacoffee.com/button-api/?text=Buy me a coffee&emoji=&slug=pjmagee&button_colour=FFDD00&font_colour=000000&font_family=Poppins&outline_colour=000000&coffee_colour=ffffff" /></a>
