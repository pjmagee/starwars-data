# Design: Search Architecture

**Status:** Reference (current state)
**Date:** 2026-04-05
**Companion docs:** [004-galaxy-map-architecture.md](004-galaxy-map-architecture.md), [003-ai-game-master.md](003-ai-game-master.md)

This document describes how search works across the StarWarsData platform — what strategies exist, which collections back them, and which features consume them. It is a snapshot of the current, cleaned-up state after consolidating the legacy `/search` endpoint and the orphaned `Pages.embedding` vector index into a single cohesive surface.

## Overview

The platform exposes **three retrieval strategies** over one unified result shape (`SearchHit`):

| Strategy | Backend | Collection | Cost | When to use |
| --- | --- | --- | --- | --- |
| **Keyword** | MongoDB `$text` index | `raw.pages` | Free (local) | Name lookups, exact title matches, "what is X" queries. |
| **Semantic** | MongoDB Atlas `$vectorSearch` + OpenAI `text-embedding-3-small` | `search.chunks` | Paid (per query embedding) | "Why / how / explain" queries, conceptual matches, paraphrased questions. |
| **Hybrid** | Parallel keyword + semantic, merged by `PageId` with overlap boost | both | Paid (semantic half) | Default for broad exploration where recall matters more than cost. |

All three strategies return [`SearchHit`](../../src/StarWarsData.Models/Search/SearchHit.cs) — the same DTO — so the Blazor UI, the galaxy map, and the AI agent toolkits consume a single shape regardless of which backend served the query.

## Component map

```
Frontend (Blazor Interactive Server)
  Components/Pages/Search.razor                  ← unified search UI (mode toggle, filters)
  Components/Pages/GalaxyMapUnified.razor        ← galaxy-map search (grid-constrained)
            │
            │ HTTP (internal, via HttpClientFactory)
            ▼
ApiService (ASP.NET Core)
  Features/Search/
    SearchController.cs                          ← GET /api/search?q=&mode=&type=&continuity=&universe=&limit=
    ArticleChunksController.cs                   ← GET /api/ArticleChunks/{pageId}/intro
  Features/GalaxyMap/
    GalaxyMapUnifiedController.cs                ← GET /api/galaxy-map/search?q=&semantic=&continuity=
  Program.cs                                     ← wires SemanticSearchService, KeywordSearchService, SearchRateLimiter,
                                                   StarWarsWikiSearchProvider into the agent pipeline
            │
            ▼
Services (StarWarsData.Services)
  Search/
    SemanticSearchService.cs                     ← $vectorSearch over search.chunks (shared by every semantic caller)
    KeywordSearchService.cs                      ← $text over raw.pages
    ArticleChunkingService.cs                    ← ETL: splits pages → chunks → embeddings
    SearchRateLimiter.cs                         ← in-memory sliding window (per user/IP)
    StarWarsWikiSearchProvider.cs                ← Agent Framework context provider (exposes keyword_search tool)
  AI/Toolkits/
    GraphRAGToolkit.cs                           ← exposes semantic_search tool to the agent
  GalaxyMap/
    MapService.cs                                ← SearchGridAsync / SemanticSearchGridAsync (projects hits onto the grid)
            │
            ▼
MongoDB (starwars database)
  raw.pages                                      ← $text index on title+content (title boosted 10×)
  search.chunks                                  ← $vectorSearch index chunks_vector_index (1536-dim, cosine)
```

## Data layer

### `raw.pages` — keyword search substrate

Raw Wookieepedia article documents. Populated by the download phase of the ETL (Phase 1). Search-relevant indexes (created by `RecordService.EnsureIndexesAsync`):

| Index | Purpose |
| --- | --- |
| `idx_text_search` | `$text` index on `title` (weight 10) + `content` (weight 1). Backs `KeywordSearchService` and `StarWarsWikiSearchProvider`. |
| `idx_title` | Ascending on `title` — regex fallback when `$text` tokenisation misses exact phrases. |
| `idx_infobox_template`, `idx_template_continuity` | Filter keyword results by type / continuity. |

### `search.chunks` — semantic search substrate

Sectioned article passages with embeddings. Populated by `ArticleChunkingService` (ETL Phase 4):

1. Read each page from `raw.pages`.
2. Strip boilerplate (navigation, refs, templates).
3. Split by markdown headings into sections; further split large sections by paragraph with a 300-char overlap.
4. Call OpenAI `text-embedding-3-small` in batches to generate 1536-dim vectors.
5. Insert chunks with `{ pageId, title, heading, wikiUrl, section, chunkIndex, text, type, continuity, universe, embedding, createdAt }`.

Indexes (created by `ArticleChunkingService`):

| Index | Kind | Purpose |
| --- | --- | --- |
| `chunks_vector_index` | Atlas `$vectorSearch`, 1536-dim, cosine | Primary semantic retrieval. |
| `ix_pageId` | ascending | Intro lookups (`/api/ArticleChunks/{pageId}/intro`). |
| `ix_type_continuity` | compound | Pre-filter candidates before vector scoring. |

The chunking pipeline is the **sole source of embeddings** in the system. The earlier scheme of embedding whole pages (`RecordService.CreateVectorIndexesAsync`, 3072-dim `Pages.embedding`) has been removed — it was never populated and confused the data model.

## HTTP surface

All search endpoints are RESTful, kebab-/lower-case, and return JSON. Semantic/hybrid endpoints enforce per-client rate limits; keyword is free.

### `GET /api/search`

Unified full-text search. The **only** general-purpose search endpoint.

| Param | Type | Default | Notes |
| --- | --- | --- | --- |
| `q` | string (required) | — | Natural language query. |
| `mode` | `keyword` \| `semantic` \| `hybrid` | `semantic` | Retrieval strategy. |
| `type` | string | — | Infobox template / entity type filter (e.g. `Character`, `System`). |
| `continuity` | `Canon` \| `Legends` \| `Both` | — | Cross-cutting continuity filter. |
| `universe` | `InUniverse` \| `OutOfUniverse` | — | Cross-cutting universe filter. |
| `limit` | int (1–25) | 10 | Max results. |

**Rate limits** (shared across all callers):

- Anonymous: **3 semantic/hybrid queries per 30 min** (keyword unlimited).
- Authenticated: **10 semantic/hybrid queries per 30 min**.
- Users who supply their own OpenAI key (BYOK) or hold the `admin` role bypass the limiter.
- `429` responses include `Retry-After` header and `{ error, limit, isAuthenticated, retryAfterSeconds }`.

**Response shape:**

```json
{
  "query": "why did Thrawn return from the Unknown Regions",
  "mode": "semantic",
  "count": 5,
  "results": [
    {
      "pageId": 12345,
      "title": "Mitth'raw'nuruodo",
      "heading": "Return to known space",
      "section": "Return_to_known_space",
      "wikiUrl": "https://starwars.fandom.com/wiki/Thrawn",
      "type": "Character",
      "continuity": "Canon",
      "score": 0.87,
      "snippet": "Following the destruction of the Chimaera …",
      "sectionUrl": "https://starwars.fandom.com/wiki/Thrawn#Return_to_known_space"
    }
  ]
}
```

### `GET /api/ArticleChunks/{pageId}/intro`

Returns the first chunk (intro section) of an article. Used by the Galaxy Map hover panel to show lightweight summaries without loading the full page. Not rate-limited.

### `GET /api/galaxy-map/search`

Galaxy-map-specific search that projects hits back onto the 26×20 grid. Documented in [004-galaxy-map-architecture.md](004-galaxy-map-architecture.md). Defaults to keyword; `?semantic=true` swaps to embedding search via `MapService.SemanticSearchGridAsync`.

This endpoint is distinct from `/api/search` because its output shape (`MapSearchResult` with `GridKey`, `MatchType`, `LinkedVia`) is grid-specific and its semantic mode performs a second pass to resolve non-grid-locatable hits through infobox location links.

## AI agent integration

The AI agent pipeline (`ApiService/Program.cs`) consumes search through **two tool surfaces**, one per strategy, so the model can choose the right tool for the question:

### `keyword_search` (via `StarWarsWikiSearchProvider`)

A Microsoft Agent Framework `MessageAIContextProvider` that exposes a single `AITool`. Backed by `$text` over `raw.pages` with a regex fallback on title. Returns a markdown-formatted block with title, wiki URL, infobox key facts, and a 500-char excerpt — designed to be injected directly into the model's context.

Tool description nudges the model toward correct use:

> Keyword search over wiki page titles and content. Fast, no AI cost. Best for exact name lookups and specific title matches. For WHY/HOW/EXPLAIN questions, use semantic_search instead.

### `semantic_search` (via `GraphRAGToolkit`)

Wraps `SemanticSearchService.SearchAsync`. Returns JSON (not markdown) so the model can reason over scores, types, and multiple hits structurally. Used alongside the KG analytics tools for grounded reasoning — search finds passages, the KG resolves entities and relationships, and the two combine in `GraphRAGToolkit.AnswerAsync`.

Both tools share the same `SemanticSearchService` / `KeywordSearchService` singletons as the HTTP endpoints, so caching, logging, and rate-limit accounting are consistent across every caller.

## Galaxy Map usage

The galaxy map integrates search in two distinct ways:

1. **Grid-constrained search box** (`/api/galaxy-map/search`) — the user types a name or phrase and the map zooms to the matching cell. Keyword mode hits `raw.pages` with `$text` and filters for documents that have a `GridSquare` infobox label. Semantic mode calls `SemanticSearchService.SearchAsync` with `types = [System, CelestialBody]`, then performs an infobox-link BFS for hits that are not themselves spatially anchored (e.g. a character's birthplace → planet → grid).

2. **Hover intros** (`/api/ArticleChunks/{pageId}/intro`) — the map requests the first article chunk for the hovered entity and renders it in the detail panel without loading the full page.

The grid controller shares `SemanticSearchService` with everything else — it is not a parallel implementation. It only adds grid projection logic on top.

## Naming and cohesion conventions

After this consolidation:

| Concept | Type / route | Location |
| --- | --- | --- |
| Unified result DTO | `SearchHit` | `Models/Search/SearchHit.cs` |
| Text strategy | `KeywordSearchService` | `Services/Search/KeywordSearchService.cs` |
| Vector strategy | `SemanticSearchService` | `Services/Search/SemanticSearchService.cs` |
| ETL (build index) | `ArticleChunkingService` | `Services/Search/ArticleChunkingService.cs` |
| Rate limiting | `SearchRateLimiter` | `Services/Search/SearchRateLimiter.cs` |
| Agent tool (keyword) | `StarWarsWikiSearchProvider` | `Services/Search/StarWarsWikiSearchProvider.cs` |
| Agent tool (semantic) | `GraphRAGToolkit.SemanticSearch` | `Services/AI/Toolkits/GraphRAGToolkit.cs` |
| Public HTTP surface | `GET /api/search` | `ApiService/Features/Search/SearchController.cs` |
| Article intros | `GET /api/ArticleChunks/{pageId}/intro` | `ApiService/Features/Search/ArticleChunksController.cs` |
| Galaxy grid search | `GET /api/galaxy-map/search` | `ApiService/Features/GalaxyMap/GalaxyMapUnifiedController.cs` |

**Principles:**

- One DTO for every caller. Heading/Section are empty strings for keyword hits rather than introducing a second shape.
- One endpoint per user-facing search surface — no parallel `/search` + `/api/SemanticSearch` legacy pair.
- One embedder, one collection (`search.chunks`). No stale whole-page embeddings.
- Tool and service names pair: the HTTP controller is thin; business logic lives in the service; the service is reused by HTTP, AI, and galaxy map without duplication.

## Removed in this consolidation

The following dead code paths existed in earlier iterations and have been removed:

| Removed | Why |
| --- | --- |
| `ApiService/Features/Pages/SearchController.cs` (`GET /search`) | Legacy endpoint; no frontend caller. Superseded by `/api/search?mode=keyword`. |
| `RecordService.GetSearchResult` | Paired with the removed controller; duplicated `KeywordSearchService`. |
| `RecordService.CreateVectorIndexesAsync` / `DeleteVectorIndexesAsync` | Created a 3072-dim `vector_index` on `raw.pages` that was never populated — embeddings live in `search.chunks`. |
| `RecordService.DeleteOpenAiEmbeddingsAsync` | Unset `Pages.embedding`, a field that no longer exists in the data model. |
| `RecordService.ProcessEmbeddingsAsync` | `throw new NotImplementedException();` stub. |
| `AdminController` endpoints `mongo/delete-embeddings` and `mongo/delete-index-embeddings` | Enqueued the dead `RecordService` methods above. |
| `Dashboard.razor` "Delete Embeddings" button | UI for the removed admin endpoint. |
| `SemanticSearchResult` (type) | Renamed to `SearchHit` — the name no longer implies a specific strategy. |
| `SemanticSearchController` + route `/api/SemanticSearch` | Renamed to `SearchController` at `/api/search` for REST consistency. |
| Three `RecordServiceTests.GetSearchResult_*` tests | Covered the removed method. |

## Future work

- **Galaxy map unification** — collapse `/api/galaxy-map/search?semantic=true|false` to `?mode=keyword|semantic` for param naming consistency with `/api/search`, or expose a single `/api/search?space=galaxy-map` projection.
- **Rate-limit keyword search** — currently free; add a cheap local limit to prevent abuse.
- **Shared hybrid merging** — `SearchController.HybridSearchAsync` and `MapService.SemanticSearchGridAsync` both merge overlapping hits; extract to a `HybridMerger` helper if a third caller appears.
- **Stop-word aware fallback** — the regex-on-title fallback is noisy for short queries; consider a proper edge-gram index.
- **Result caching** — semantic queries cost real money; a 5-minute in-memory LRU keyed by `(q, mode, filters)` would pay for itself on the landing page's "popular queries" list.
