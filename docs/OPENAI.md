# OpenAI Integration

## Models

| Model | Purpose |
| ----- | ------- |
| Configurable via `Settings:OpenAiModel` | Primary AI agent (tool-calling, reasoning) |
| `gpt-4o-mini` | Topic guardrail classifier, lightweight IChatClient |
| `text-embedding-3-small` | Vector embeddings for MongoDB Atlas search |

## AI Agent

The AI agent is built with the [Microsoft Agents AI](https://github.com/microsoft/agents) framework (`Microsoft.Agents.AI` v1.0.0-rc4) and served via the AGUI protocol over SSE at `/kernel/stream`.

### Tool Registry

The agent has access to three categories of tools:

**ComponentToolkit** (7 render tools) - Output visualization:

- `render_table` - Paginated table by infobox type (frontend-fetched)
- `render_data_table` - Ad-hoc table with inline row data
- `render_chart` - Bar, Line, Pie, Donut, StackedBar, TimeSeries, Radar
- `render_graph` - Relationship/family tree graphs (frontend-fetched)
- `render_timeline` - Temporal event timelines (frontend-fetched)
- `render_infobox` - Wiki-style entity info cards (frontend-fetched)
- `render_text` - Text summaries and article excerpts

**DataExplorerToolkit** - Data querying:

- `search_pages_by_name` - Search pages by infobox type and name
- `get_page_by_id` - Get full page infobox data
- `get_page_property` - Get specific property from a page
- `sample_property_values` - Sample values for a property label
- `list_infobox_types` - List available infobox types
- `list_timeline_categories` - List timeline event categories

**MongoDB MCP Server** (`@mongodb-js/mongodb-mcp-server`, read-only):

- `find` - Query documents
- `aggregate` - Aggregation pipelines
- `count` - Count documents

**Wiki Search (RAG)**:

- `search_wiki` - Regex text search against the Pages collection for lore/history context

### Topic Guardrail

A lightweight `gpt-4o-mini` classifier runs before the main agent to reject off-topic queries. Only Star Wars universe questions are allowed through.

### Source References

All render tools accept an optional `references` parameter containing page titles and Wookieepedia URLs. The frontend renders these as a "Sources" footer section with clickable links. The `search_wiki` tool returns source URLs that the agent extracts into references.

## Embeddings

Used for MongoDB Atlas vector search. Documents are vectorized using `text-embedding-3-small` and stored as float arrays in an `embeddings` field.

The embedding pipeline:

1. Download pages from Wookieepedia and store as raw documents
2. Process relationships and infobox data
3. Upload to MongoDB Atlas
4. Run the embeddings job to generate vectors for each document
5. Create vector indexes for similarity search

Reference: [OpenAI Embeddings Guide](https://platform.openai.com/docs/guides/embeddings#embedding-models)

## Observability

GenAI telemetry is emitted via the `Experimental.Microsoft.Extensions.AI` ActivitySource and exported through OpenTelemetry to the Aspire dashboard. Sensitive data is enabled on the `UseOpenTelemetry()` middleware for debugging tool calls and token usage.
