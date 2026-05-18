# Design-037: Miscellaneous — public site-activity dashboard

**Status:** Implemented & browser-validated 2026-05-18 (Phase 1 + Phase 2) — `CorpusStatsService` (`Services/Stats/`, `IMemoryCache` 5-min snapshot, `EstimatedDocumentCount`), `StatsController` (`api/stats/corpus`, `recent/pages`, `recent/chunks`, `pages`, `chunks`), `StarWarsData.Models.Stats` DTOs (the paged wrapper is named `StatsPage<T>` to avoid colliding with the existing `Models.Queries.PagedResult<T>`), the Frontend "Miscellaneous" nav section, and pages `SiteActivity` (`/activity`), `WikiSyncBrowse` (`/activity/wiki-sync`), `ArticleChunksBrowse` (`/activity/chunks`). All five endpoints verified end-to-end against live `starwars-dev` (166,424 nodes / 594,615 edges / 224,796 pages / 817,661 chunks; recent + paginated browse correct). **Browser-validated** (Chrome DevTools MCP, 2026-05-18): `/activity` renders the banner ("18 d ago"), four stat tiles, and both recent tables with continuity chips on desktop **and** 414×896 mobile; `/activity/wiki-sync` pager "1-25 of 224,796"; `/activity/chunks` pager "1-25 of 166,422"; console clean (one pre-existing app-wide 404 asset unrelated to this feature; Blazor circuit healthy); the snapshot caption advancing "just now"→"1 min ago" across navigations confirms the `IMemoryCache` 5-min TTL serves cached data; the global-filter exemption holds (header Canon/Legends switches present but the pages do not react — by design per ADR-009).
**Date:** 2026-05-18
**Author:** Patrick Magee + Claude
**Companion docs:** [ADR-009 Public read-only corpus-stats surface](../adr/009-public-readonly-corpus-stats-surface.md), [ADR-001 Internal API Auth](../adr/001-internal-api-auth.md), [ADR-002 Three-Project Blazor Server + Shared API](../adr/002-three-project-blazor-server-shared-api.md)

## Problem

The public Frontend gives no signal that the corpus is alive and maintained. The daily wiki sync (03:00 UTC), the infobox graph rebuild, and article chunking all run silently server-side. A curious visitor has no way to see "this isn't an abandoned data dump — pages are still syncing, the graph keeps growing." There is also no public, read-only way to browse *what* recently synced or got chunked.

The numbers that would convey this — total KG nodes/edges, total article pages, total chunks, last sync time, most-recently synced/chunked articles — already exist, but only inside the **Admin** app, which is internal-only and unreachable from the public site ([ADR-002](../adr/002-three-project-blazor-server-shared-api.md)).

## Goals

1. A public **"Miscellaneous"** nav section on the Frontend with a **Site Activity** dashboard that shows, at a glance: total KG node count, total edge count, total article pages, total article chunks, and when the wiki sync last ran.
2. **Top 10 recently modified** lists: articles synced (from `raw.pages`) and articles chunked (from `search.chunks`).
3. A **read-only browse** view of synced wiki pages and of article chunks for visitors who want to dig in.
4. Read-only, zero-action, public — it informs; it never mutates and needs no login.
5. Cheap and safe under anonymous traffic — no per-request corpus scans against the shared `mongod` ([ADR-009](../adr/009-public-readonly-corpus-stats-surface.md)).

## Non-goals

- Any write/trigger/admin capability. ETL/job control stays in the Admin app.
- Real-time/exact figures. A periodically-refreshed snapshot is the explicit design point ([ADR-009](../adr/009-public-readonly-corpus-stats-surface.md)).
- Continuity/realm filtering. These are corpus-infrastructure totals, **exempt from the global filter** per [ADR-009](../adr/009-public-readonly-corpus-stats-surface.md). The Miscellaneous pages do not subscribe to `GlobalFilterService`.
- Per-user analytics, traffic stats, or anything sourced from `chat.*` / `admin.*` / user data.
- Reusing or exposing Admin endpoints (see ADR-009 alternatives).

## API design (ApiService — new `Features/Stats`)

New `src/StarWarsData.ApiService/Features/Stats/StatsController.cs`, `[Route("api/[controller]")]`, no `[Authorize]` (public, like `Features/Costs/CostsController.cs`). Backed by a new `CorpusStatsService` in `src/StarWarsData.Services/Stats/`.

| Endpoint | Returns | Caching |
| --- | --- | --- |
| `GET /api/stats/corpus` | `CorpusStatsDto` — the four totals + `LastWikiSyncUtc` + `SnapshotUtc` | `IMemoryCache`, single key, TTL 5 min |
| `GET /api/stats/recent/pages?limit=10` | `List<RecentArticleDto>` (max 25) | folded into the same 5-min snapshot |
| `GET /api/stats/recent/chunks?limit=10` | `List<RecentChunkDto>` (max 25) | folded into the same 5-min snapshot |
| `GET /api/stats/pages?skip=&take=` *(Phase 2)* | paginated synced pages | uncached; indexed `Sort(downloadedAt desc).Skip.Limit`, `take ≤ 100` |
| `GET /api/stats/chunks?skip=&take=` *(Phase 2)* | paginated chunk groups | uncached; bounded aggregation |

DTOs — new namespace `StarWarsData.Models.Stats`:

```csharp
public sealed class CorpusStatsDto
{
    public long KgNodes { get; init; }          // EstimatedDocumentCount(Collections.KgNodes)
    public long KgEdges { get; init; }          // EstimatedDocumentCount(Collections.KgEdges)
    public long ArticlePages { get; init; }     // EstimatedDocumentCount(Collections.Pages)
    public long ArticleChunks { get; init; }    // EstimatedDocumentCount(Collections.SearchChunks)
    public DateTime? LastWikiSyncUtc { get; init; } // raw.job_state JobName == IncrementalSync .UpdatedAt
    public DateTime SnapshotUtc { get; init; }      // when this cached snapshot was built
}

public sealed class RecentArticleDto
{
    public int PageId { get; init; }
    public string Title { get; init; } = "";
    public DateTime LastModified { get; init; }   // Page.LastModified (wiki revision)
    public DateTime DownloadedAt { get; init; }   // Page.DownloadedAt (our sync)
    public string Continuity { get; init; } = ""; // display only — NOT a filter
}

public sealed class RecentChunkDto
{
    public int NodeId { get; init; }
    public string Title { get; init; } = "";
    public int ChunkCount { get; init; }
    public DateTime CreatedAt { get; init; }      // ArticleChunk.CreatedAt (max within node)
}
```

### `CorpusStatsService` (Services/Stats/)

Primary-ctor `(IMongoClient mongoClient, IOptions<SettingsOptions> settings, IMemoryCache cache, ILogger<CorpusStatsService> logger)`. Owns its own thin Mongo access — **must not** depend on `PageDownloader`/`InfoboxGraphService`/`ArticleChunkingService` (keeps the public path off ETL/admin surface — ADR-009).

- **Totals:** `EstimatedDocumentCountAsync` on `Collections.KgNodes`, `KgEdges`, `Pages`, `SearchChunks` (O(1) metadata; never scans).
- **Last sync:** `raw.job_state` find `JobName == "IncrementalSync"`, project `UpdatedAt` (mirrors `PageDownloader.GetRecentSyncStatusAsync` logic without taking the heavy dependency).
- **Recent pages:** `raw.pages` `Sort(downloadedAt desc).Limit(n)` projected to `RecentArticleDto`.
- **Recent chunks:** `search.chunks` aggregation `$group` by `nodeId` → `max(createdAt)`, `count`, `$sort` desc, `$limit n`, `$lookup` `raw.pages` for `title`.
- One `GetSnapshotAsync(ct)` builds totals + both recent lists + last-sync into one record cached under a single key, TTL 5 min. Phase-2 browse methods are separate and uncached (bounded, indexed).

Register in `ApiService/Program.cs` DI (the ApiService has `IMemoryCache` available via `AddMemoryCache()` if not already present — add if missing).

## Frontend UX & layout (MudBlazor)

### Navigation

In `src/StarWarsData.Frontend/Components/Layout/NavMenu.razor`, add a new section header + links following the existing `MudText` section / `MudNavLink` pattern (same shape as the "Static Pages" group, **always visible** — these are lightweight public info pages and useful on mobile):

```razor
<MudText Typo="Typo.overline" Class="px-4 mt-4 mud-text-secondary">Miscellaneous</MudText>
<MudNavLink Href="/activity"          Icon="@Icons.Material.Filled.Insights">Site Activity</MudNavLink>
<MudNavLink Href="/activity/wiki-sync" Icon="@Icons.Material.Filled.Sync">Wiki Sync</MudNavLink>
<MudNavLink Href="/activity/chunks"   Icon="@Icons.Material.Filled.Dataset">Article Chunks</MudNavLink>
```

### Pages

**`/activity` — Site Activity (Phase 1, primary).** `Components/Pages/SiteActivity.razor`.

- `@inject IHttpClientFactory HttpClientFactory` → `CreateClient("StarWarsData")`. **No** `GlobalFilterService` injection/subscription (ADR-009 exemption).
- **Last-sync banner:** `MudAlert Severity="Severity.Success" Variant="Variant.Outlined"` — "Wiki sync last ran {relative time} ago — the corpus is actively maintained." If `LastWikiSyncUtc` is older than ~48 h, downgrade to `Severity.Info` (no alarming colours on a public page).
- **Stat tiles:** `MudGrid` of four `MudItem xs="6" md="3"`, each a `MudPaper Outlined` with a `MudStack` — `MudIcon` + `MudText Typo="Typo.h4"` (formatted `n0`, e.g. `595,159`) + `MudText Typo="Typo.caption"` label (KG Nodes / KG Edges / Article Pages / Article Chunks).
- **Two "Top 10 recently…" panels** side by side (`MudGrid` → `MudItem xs="12" md="6"`), each a `MudPaper Outlined` with a read-only `MudTable Dense Hover` (no row click handlers, no actions column): *Recently Synced Articles* (Title, Last Modified, Synced) and *Recently Chunked Articles* (Title, Chunks, Chunked). Continuity rendered as a `MudChip` following the **continuity colour convention** (Canon→`Color.Primary`, Legends→`Color.Secondary`, else `Color.Default` — see `ContinuityBadge.razor`). Each panel footer: a `MudButton Variant="Text"` "Browse all →" linking to the corresponding Phase-2 page.
- States: `MudProgressCircular` (or `MudSkeleton` tiles) while loading; `MudAlert Severity="Severity.Normal"` on empty; `MudAlert Severity="Severity.Warning"` with a retry button on fetch failure (never a raw exception).
- Footer caption: "Figures refresh every few minutes." (sets expectations per ADR-009).

**`/activity/wiki-sync` — read-only synced pages (Phase 2).** `Components/Pages/WikiSyncBrowse.razor`. Server-paged `MudTable` (`ServerData`) over `GET /api/stats/pages` — Title, Page ID, Last Modified, Synced At, Continuity chip. Read-only; Title links to the existing on-site page view if one exists, else plain text. No edit/delete.

**`/activity/chunks` — read-only chunk browse (Phase 2).** `Components/Pages/ArticleChunksBrowse.razor`. Server-paged `MudTable` over `GET /api/stats/chunks` — Title, Node ID, Chunk Count, Created. Read-only. (This is the public, safe analogue of the Admin `ArticleChunks.razor` progress view — counts only, no job controls.)

## Phasing

- **Phase 1 (core ask):** `CorpusStatsService` + `/api/stats/corpus` + `/api/stats/recent/{pages,chunks}`; `SiteActivity.razor` at `/activity`; the Miscellaneous nav section. Delivers every metric the request named.
- **Phase 2 (the "read-only browse" ask):** paginated `/api/stats/pages` & `/api/stats/chunks`; `WikiSyncBrowse.razor` + `ArticleChunksBrowse.razor`; wire the "Browse all →" buttons.

## Open questions

1. **Route base** — `/activity` (chosen; communicates "the site is active") vs `/misc` vs `/stats`. Proposed: `/activity` with the nav section labelled "Miscellaneous" per the request.
2. **Recently-chunked title source** — `$lookup` into `raw.pages` (proposed; authoritative title) vs `kg.nodes`. `raw.pages` chosen since chunking is page-driven and the title field is canonical there.
3. **"Browse all" on mobile** — Phase-2 tables are wide; on xs, fall back to a stacked `MudCard` list (same data, card layout) rather than a horizontally-scrolling table. Decide during Phase 2 UI validation.
4. **Show `infobox graph last rebuilt` / `chunking last run`** too? The request named "sync jobs working"; last *wiki sync* covers the core signal. Additional job timestamps can be added to `CorpusStatsDto` later from `raw.job_state` without an API shape change — deferred.

## Validation

Per CLAUDE.md, the Frontend pages must be validated against a running browser via Chrome DevTools MCP (desktop + 414×896 mobile, console clean) before being marked shipped — not just build-green. The dashboard's read-only, filter-exempt behaviour (no `GlobalFilterService` wiring) and the continuity-colour convention on chips are explicit review items.

## References

- [ADR-009](../adr/009-public-readonly-corpus-stats-surface.md) — the architectural decision (public read-only surface, caching, global-filter exemption) this design implements.
- Grounding symbols: `Collections.{KgNodes,KgEdges,Pages,SearchChunks,JobState}` (`src/StarWarsData.Models/Settings.cs`); `Page.{LastModified,DownloadedAt}`, `ArticleChunk.CreatedAt`, `JobState.{JobName,UpdatedAt}`, `RecentSyncStatus`; `PageDownloader.GetRecentSyncStatusAsync` (logic mirror, not a dependency); `CostsController` (public read-only controller pattern); `KnowledgeGraph.razor` (Frontend API-call + MudBlazor pattern); `ContinuityBadge.razor` (continuity colour convention).
