# Design: Galaxy Map — Current Architecture

**Status:** Reference (current state)
**Date:** 2026-04-05
**Companion docs:** [001-temporal-facets.md](001-temporal-facets.md), [003-galaxy-map-temporal.md](003-galaxy-map-temporal.md)

This document describes how the Galaxy Map page is built, served, and rendered **today**. It is a snapshot of current state — future improvements live in `003-galaxy-map-temporal.md`.

## Overview

The Galaxy Map is a single Blazor page ([GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor), ~1400 lines) with **two interaction modes** driven by one shared D3 canvas:

| Mode | Purpose | Primary data |
| --- | --- | --- |
| **Explore** | Navigate the galaxy geographically. Drill overview → region → grid cell → system → planet. Keyword and semantic search jump the viewport to a specific grid cell and open a detail panel. | Static geography (regions, sectors, grid, systems/planets) derived from Wookieepedia infoboxes via `raw.pages`. |
| **Timeline** | Scrub year-by-year through galactic history. Faction control shades regions; era bands and event density drive a playback bar. | Pre-baked per-year snapshots from `galaxy.years` + event/territory overlays. |

Mode switching is client-side — the D3 module (`js/galaxy-map-unified.js`) keeps the same scene graph and swaps the temporal overlay layer on/off.

## Component map

```
Frontend (Blazor Interactive Server)
  Components/Pages/GalaxyMapUnified.razor          ← single page, both modes
  wwwroot/js/galaxy-map-unified.js                 ← D3 renderer, pan/zoom, levels
            │
            │ HTTP (internal, via HttpClientFactory)
            ▼
ApiService (ASP.NET Core)
  Features/GalaxyMap/
    GalaxyMapUnifiedController.cs   route: api/galaxy-map/*   ← sole HTTP surface
            │
            ▼
Services (StarWarsData.Services/GalaxyMap)
  GalaxyMapReadService.cs      ← reads galaxy.years (runtime, zero aggregation)
  MapService.cs                ← geography, viewport systems, keyword + semantic search
  GalaxyMapETLService.cs       ← writes galaxy.years (ETL Phase 8)
  TerritoryInferenceService.cs ← writes territory.* (ETL Phase 7)
  GalacticRegions.cs           ← canonical region-name normaliser
  FactionColorPalette.cs       ← deterministic faction colouring
```

DI registration for the ETL service is in [Admin/Program.cs:57](../../src/StarWarsData.Admin/Program.cs#L57). The ETL is triggered via [AdminController.BuildGalaxyMap](../../src/StarWarsData.Admin/Features/Admin/AdminController.cs#L496) and exposed as an Aspire HTTP command ("8. Build Galaxy Map") from [AppHost/Program.cs:226](../../src/StarWarsData.AppHost/Program.cs#L226).

## MongoDB collections

All collections live in the `starwars` (prod) / `starwars-dev` (dev) database. Namespacing follows the project convention (`raw.*`, `kg.*`, `timeline.*`, `galaxy.*`, `territory.*`).

### Read at runtime (per page request)

| Collection | Purpose | Used by |
| --- | --- | --- |
| `galaxy.years` | Single collection storing both `GalaxyYearDocument` (one per year) and a single `GalaxyOverviewDocument` (`_id: "overview"`) with factions, eras, available-years list. Timeline mode events are embedded directly in each year document, so no separate event-lens API is needed at runtime. | `GalaxyMapReadService` → all overview/year/factions reads. |
| `raw.pages` | Source Wookieepedia pages with infobox data. Queried for `Grid square`, `Region`, `System`, `CelestialBody` infobox templates to build the static geography overlay and resolve viewport systems. Also feeds keyword search. | `MapService`. |
| `search.chunks` | Embedded article chunks for semantic search. | `MapService.SemanticSearchGridAsync` (Explore search → `api/galaxy-map/search?semantic=true`). |

`territory.years` and `territory.snapshots` are written by Phase 7 (`TerritoryInferenceService`) and consumed **only** by Phase 8 (`GalaxyMapETLService`), which folds territory data into `galaxy.years` at build time. They have no runtime readers.

### Written by the ETL (not read at runtime)

| Collection | Read by ETL | Written by |
| --- | --- | --- |
| `kg.nodes` | `GalaxyMapETLService`, `TerritoryInferenceService` — governments, eras, systems, planets, factions, events (any node with a `StartYear`). | Phase 6 (KG builder). |
| `kg.edges` | `GalaxyMapETLService`, `TerritoryInferenceService` — `in_region`, `affiliated_with` (with `FromYear`/`ToYear`), plus full adjacency list for BFS location resolution. | Phase 6. |
| `raw.pages` | `GalaxyMapETLService` — reads infobox `Grid square` + `Region` values to build `planetToGrid` / `nameToGrid` lookups and resolves faction pages for icons/wiki URLs. | Phase 1. |

Collection name constants live in [Collections](../../src/StarWarsData.Models/Settings.cs) (`GalaxyYears`, `TerritoryYears`, `TerritorySnapshots`, `KgNodes`, `KgEdges`, `Pages`, `SearchChunks`).

## HTTP API surface

All endpoints are internal (not internet-exposed); the Frontend calls them via the authenticated `HttpClient` with an `X-User-Id` header set by the `DelegatingHandler` (see [ADR 001](../adr/001-internal-api-auth.md)).

The feature has a single controller — [GalaxyMapUnifiedController.cs](../../src/StarWarsData.ApiService/Features/GalaxyMap/GalaxyMapUnifiedController.cs) at `api/galaxy-map`. All routes are kebab-case, plural-noun, and share `?continuity=` as a cross-cutting filter.

| Endpoint | Purpose | Source | Cache |
| --- | --- | --- | --- |
| `GET api/galaxy-map` | Top-level overview — factions, eras, available years. | `galaxy.years` (`_id: "overview"`) | 600 s |
| `GET api/galaxy-map/years` | List year snapshots, optional `?from=&to=` range. | `galaxy.years` | 300 s |
| `GET api/galaxy-map/years/{year}` | Single per-year snapshot (region control, events, governments). | `galaxy.years` | 600 s |
| `GET api/galaxy-map/factions` | Faction metadata map (name → colour/icon/wiki URL). | `galaxy.years` overview doc | 600 s |
| `GET api/galaxy-map/geography` | Static geography: regions, sectors, trade routes, nebulas, grid bounds. | `raw.pages` (infobox filters) | 300 s |
| `GET api/galaxy-map/systems?minCol=&maxCol=&minRow=&maxRow=` | Viewport-scoped systems, lazy-loaded as the user pans. | `raw.pages` | 120 s |
| `GET api/galaxy-map/search?q=&semantic=false` | Unified search. `semantic=false` (default) runs keyword match against grid-locatable pages; `semantic=true` runs embedding search over chunks and projects hits onto the grid. | `raw.pages` (+ `search.chunks` when semantic) | — |

Before the 2026-04-05 refactor the feature exposed four controllers (`GalaxyMap/*`, `api/galaxy-map/*`, `api/GalaxyEvents/*`, `api/TerritoryControl/*`) and ~1,200 lines of dead code. Everything except the unified controller was unreachable from the frontend and has been deleted. The event-lens API that `api/GalaxyEvents/*` once exposed is unnecessary because event data is pre-baked into each `galaxy.years` document at ETL time.

## ETL pipeline (Phase 8 — Build Galaxy Map)

Entry point: [GalaxyMapETLService.BuildGalaxyMapAsync](../../src/StarWarsData.Services/GalaxyMap/GalaxyMapETLService.cs).

Depends on: Phase 1 (raw pages), Phase 6 (KG nodes + edges), Phase 7 (territory snapshots, optional — baked into `galaxy.years` via `TerritoryInferenceService`).

Steps:

1. **Load territory source** from `kg.nodes` (governments, factions) and `kg.edges` (`in_region`, `affiliated_with` with `fromYear`/`toYear`). Build `planetToRegion` and `temporalAffiliations : (faction, region, fromYear, toYear)`.
2. **Build grid lookup** by scanning `raw.pages` for infoboxes with `Grid square` + `Region` labels. Produces `planetToGrid : pageId → (col, row)` and `nameToGrid` for fuzzy lookup. Grid bounds are computed from the data (not hardcoded).
3. **Build adjacency list** from *all* edges (across continuities) so BFS can resolve an event's location by walking from an event node to its nearest planet/system with a grid cell.
4. **Load event nodes** from `kg.nodes` where `StartYear is not null`. Group by type (= lens) and by year. For conflict/institutional nodes with an end year, span all years in the range (capped at 100 to prevent pathological wars). See `003-galaxy-map-temporal.md` §5, §8 for the known limitations here (calendar filter, first-facet heuristic).
5. **Load era nodes** from `kg.nodes` (`type = Era`).
6. **Collect all years** from governments + events, then for each year compute `ComputeRegionControls` by filtering `temporalAffiliations` on `fromYear <= year <= toYear`.
7. **Write `galaxy.years`**: one `GalaxyYearDocument` per year + one `GalaxyOverviewDocument` with factions, eras, available years.

The ETL is idempotent — `galaxy.years` is dropped and rebuilt each run. On a full rebuild it also regenerates `territory.*` via `TerritoryInferenceService` (Phase 7) which reuses `kg.edges` temporal bounds, battle outcomes, and government lifecycle facets to produce per-event snapshots.

## Rendering (frontend)

- **Page init** ([LoadData](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor#L898)) fetches `api/galaxy-map/geography` + `api/galaxy-map` + `api/galaxy-map/factions` in parallel, seeds `_year` to the first available year, then hands the geography payload to `js/galaxy-map-unified.js` via `initialize(elementId, geography, dotNetRef)`.
- **Explore** uses JS-invokable callbacks (`FetchSystemsInRange`, `OnLevelChanged`, `OnRegionHovered`, `OnSystemSelected`, `OnCelestialBodySelected`) so D3 can lazy-load systems (`api/galaxy-map/systems?minCol=&maxCol=&minRow=&maxRow=`) as the viewport changes and bubble selections back to Blazor for the detail panel.
- **Timeline mode** (`OnModeChanged("timeline")`) calls `LoadYear` which fetches `api/galaxy-map/years/{year}` and invokes the D3 `renderTemporalLayers` overlay. `clearTemporalLayers` is called when switching back to Explore so the scene graph is clean.
- **Search** is a single endpoint (`api/galaxy-map/search?q=&semantic=`); the page toggles `semantic=true` when the user flips the semantic switch.
- **Detail panels** fetch article intros from `api/ArticleChunks/{pageId}/intro` on hover/click (cached server-side).
- **Continuity and universe filters** come from `GlobalFilterService`; every request appends `?continuity=…` where relevant.

## Key design choices (recorded)

1. **Pre-baked per-year snapshots.** Runtime queries never aggregate across years; they read a single document from `galaxy.years`. This keeps Timeline scrubbing instant at the cost of an ETL dependency after any KG change.
2. **One page, two modes, one controller.** A single D3 scene graph is reused across modes, and a single `GalaxyMapUnifiedController` exposes the entire HTTP surface. Any feature that can't be served from `galaxy.years` (geography, viewport systems, search) delegates to `MapService`; everything temporal delegates to `GalaxyMapReadService`.
3. **Geography from `raw.pages`, not KG.** Grid coordinates live on Wookieepedia infoboxes and are read directly from `raw.pages`. The KG is used only for temporal and relational data (who controlled what, and when). If Phase 1 changes, geography changes; if Phase 6 changes, territory/events change.
4. **Territory is folded into `galaxy.years`.** `territory.years` and `territory.snapshots` exist only as ETL intermediates. The Phase 8 build reads them once and embeds the results into each year document so the runtime has a single source of truth.
5. **Unified search endpoint.** Keyword and semantic search share `/search?q=&semantic=` rather than two separate routes with divergent parameter names — the Explore UI toggles `semantic` when the user flips the switch. Timeline mode intentionally does not expose search.

## Known gaps (tracked in 003)

- Multi-year event spanning relies on a first-facet heuristic, not all `TemporalFacets`.
- Event inclusion does not filter by `Calendar == "galactic"`, so real-world publication dates leak onto the map.
- Battle-derived territorial presence (`belligerent` + `took_place_at`) is not yet used for region control; only formal `affiliated_with` edges are counted.
- Government lifecycle transitions (`institutional.fragmented`/`restored`) collapse into a single envelope, so the Empire appears continuously from 19 BBY to 35 ABY instead of showing the 5–21 ABY gap.
- Edge temporal bounds are mostly derived (lifespan overlap), not explicit — see [001-temporal-facets.md](001-temporal-facets.md) for the `TemporalExplicit` flag proposal.
