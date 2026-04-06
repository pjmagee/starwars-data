# Design: Galaxy Map — Temporal Explore vs. Timeline Modes

**Status:** Draft
**Date:** 2026-04-05
**Companion docs:** [003-galaxy-map-temporal.md](003-galaxy-map-temporal.md), [004-galaxy-map-architecture.md](004-galaxy-map-architecture.md), [001-temporal-facets.md](001-temporal-facets.md)

## Problem

The Galaxy Map has two modes (Explore and Timeline) but only the **overlays** (territory shading, event heatmap, markers) are temporal. The **base geography layer** — regions, trade routes, celestial bodies, nebulas, systems — is a single static snapshot rendered identically in both modes. Scrubbing the timeline to 100 BBY still shows Starkiller Base, Alderaan is alive in 4 ABY, trade routes that were not established until the High Republic appear in the Old Republic, and Death Star II orbits Endor in 200 BBY.

The KG already carries the data needed to fix this (`construction.start/end`, `creation.start/end`, `institutional.start/end`, edge temporal bounds) — the ETL and rendering pipeline just ignore it for the base layer. See `003-galaxy-map-temporal.md` §6–§8 for the adjacent territory/events work that has already been designed.

## Goals

Two clearly-separated modes with an unambiguous contract:

| Mode | Base geography | Overlays |
| --- | --- | --- |
| **Explore** | Everything, always. All regions, all trade routes, all celestial bodies, all nebulas, all systems. No year — the mode is atemporal. | None. (Territory + events are Timeline-only.) |
| **Timeline** | Filtered/annotated by current year. Trade routes render only during their active window. Destroyed planets either hide or render as "destroyed". Structures (Death Stars, Starkiller, Starforge) appear only during their construction lifespan. | Existing per-year territory shading + event heatmap (from `galaxy.years`). |

Explore mode is already close to correct today. **All of the work is in Timeline mode.**

### Non-goals

- Moving stars/systems across the grid over time (canonically they don't move meaningfully at this scale).
- Rendering sector/region boundary changes over time (political, not geographical; regions are effectively stable).
- Character trajectories (that's CharacterTimeline territory, not the galaxy map).
- Year-sensitive nebulas (they don't have meaningful temporal bounds in canon).

## Data available from the KG

The signals below already exist in `kg.nodes` / `kg.edges` after the 2026-04-04 rebuild:

| Signal | Source | Covers |
| --- | --- | --- |
| `construction.start` / `construction.end` / `construction.rebuilt` | `TemporalFacet.Semantic` on nodes | Celestial bodies destroyed (Alderaan, Jedha, Hosnian Prime, Scarif), structures built/destroyed (Death Stars, Starkiller, Starforge, Citadel Station), starships commissioned/retired |
| `creation.start` / `creation.end` / `creation.discovered` | `TemporalFacet.Semantic` | Artifacts, devices — less relevant to the map |
| `institutional.start` / `end` / `reorganized` / `restored` / `fragmented` | `TemporalFacet.Semantic` on Government / Organization | Faction lifecycle (already designed in `003-galaxy-map-temporal.md` §7) |
| Edge `FromYear` / `ToYear` on `end_points`, `transit_points`, `has_object`, `affiliated_with`, `has_capital` | `RelationshipEdge` | When each relationship was active |
| `took_place_at` + `belligerent` from Battle nodes | `kg.edges` | Military presence inference (already designed in `003-galaxy-map-temporal.md` §6) |

What's **not** in the KG today:

- Trade route `established` / `discontinued` dates are rarely on the infobox directly; some routes only have "active during the Old Republic era" as prose. Temporal bounds on trade-route edges are therefore mostly **derived** (lifespan overlap of endpoints) — see `001-temporal-facets.md` §"Explicit vs Derived Edge Temporal Bounds". Without an explicit flag, trade-route temporal filtering will be noisy.
- Nebulas don't have meaningful temporal facets.
- "This planet was razed / depopulated but not destroyed" states (Mandalore, Mon Cala during the Empire). These are political/cultural and better represented via the existing territory overlay than via geography filtering.

## Proposal

### Core idea: temporal annotations, not temporal geography

**Do not** rebuild the geography payload per year. The static geography (~300 regions/nebulas/cells, ~thousands of systems, ~hundreds of trade routes) already loads once on page init. Building per-year copies would multiply that by the ~100 available years and ruin the Timeline scrub experience.

Instead, attach **optional temporal metadata** to the existing geography entities and **filter client-side in JS** when the year changes in Timeline mode. The filter is a single pass over already-loaded data; no HTTP round trips during scrubbing.

### Model changes

Add optional temporal fields to the existing DTOs in [GalaxyGeography.cs](../../src/StarWarsData.Models/GalaxyMap/GalaxyGeography.cs):

```csharp
public class GeoCelestialBody {
    // ... existing fields ...
    public int? StartYear { get; set; }      // constructed / colonised
    public int? EndYear { get; set; }        // destroyed / abandoned — null = still present
    public string? EndReason { get; set; }   // "destroyed" | "abandoned" | "rebuilt"
}

public class GeoSystem {
    // ... existing fields ...
    // A system is "gone" only if ALL of its bodies are gone.
    // Computed from child bodies at ETL time, not stored directly.
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}

public class GeoTradeRoute {
    // ... existing fields ...
    public int? StartYear { get; set; }      // Earliest use (explicit facet, else min endpoint start)
    public int? EndYear { get; set; }        // Last known use (explicit facet, else max endpoint end)
    public bool TemporalExplicit { get; set; } // true if from explicit facets; false if derived
}
```

Nebulas and regions stay atemporal.

Also add a new node type to the `GalaxyOverviewDocument` for transient structures that aren't today rendered on the map:

```csharp
public class GeoStructure {
    public int PageId { get; set; }
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";     // "battle station" | "base" | "shipyard" | ...
    public int Col { get; set; }
    public int Row { get; set; }
    public string? Region { get; set; }
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public string? EndReason { get; set; }
}
```

This captures Death Stars, Starkiller Base, Echo Base, Starforge, Citadel Station, and similar — entities that have a `construction.*` facet chain and a `took_place_at` or `in_region` edge that resolves to a grid cell.

### ETL changes

Temporal annotations are populated by **Phase 8** (`GalaxyMapETLService`), which already builds both `galaxy.years` and touches every KG node/edge. Two options for where the annotations live:

**Option A (recommended): bake into `GalaxyOverviewDocument`.** Already a single document; already loaded on page init via `api/galaxy-map`. Add a sibling collection/field per entity type:

```csharp
public class GalaxyOverviewDocument {
    // ... existing ...
    public List<GeoBodyTemporal> BodyTemporal { get; set; } = [];         // sparse: only bodies with temporal facets
    public List<GeoStructure> Structures { get; set; } = [];
    public Dictionary<int, (int? start, int? end)> RouteTemporal { get; set; } = [];
}
```

Only entities that actually have temporal facets appear in these lists (sparse). Expected counts: ~50–200 destroyed bodies, ~30–60 structures, ~100–400 trade routes with derived bounds. Tiny payload increase.

**Option B: separate collection `galaxy.temporal`.** Cleaner separation, extra HTTP call. Not worth it at this size.

Build steps inside `GalaxyMapETLService.BuildGalaxyMapAsync` (extending the existing Phase 8 pass — no new phase needed):

1. After step 4 ("load event nodes"), also project `construction.start/end/rebuilt` facets from CelestialBody / SpaceStation / Structure nodes. Write them into `BodyTemporal` / `Structures`.
2. When building trade routes (current step 11), read `FromYear`/`ToYear` from each endpoint/transit-point edge and take the envelope. Flag `TemporalExplicit=false` until `001-temporal-facets.md`'s explicit-vs-derived flag lands; then prefer explicit.
3. For each `Structure` node that has both a `construction.*` facet and a resolvable grid cell (direct `took_place_at`/`located_in` edge, or 2-hop BFS through the adjacency list already built for events), emit a `GeoStructure` entry.

`MapService.GetGeographyAsync` stays mostly unchanged — it still reads `raw.pages` for regions/cells/nebulas/trade-route geometry. The temporal annotations are merged into the response from `GalaxyOverviewDocument` (or delivered on a separate endpoint and zipped client-side).

### Frontend changes

[galaxy-map-unified.js](../../src/StarWarsData.Frontend/wwwroot/js/galaxy-map-unified.js) today has `renderTemporalLayers` + `clearTemporalLayers` for overlays only. Add symmetric entry points for the **base layer**:

```js
export function applyTemporalBaseFilter(year) {
    // Hide/mark base-layer entities based on year.
    // Called from Blazor OnYearChanged (Timeline mode).
}
export function clearTemporalBaseFilter() {
    // Restore full visibility for Explore mode.
}
```

Implementation is a pass over the already-rendered SVG groups tagged with `data-start-year` / `data-end-year` attributes written at draw time:

```js
function applyTemporalBaseFilter(year) {
    svg.selectAll('[data-start-year], [data-end-year]').each(function () {
        const s = +this.dataset.startYear || -Infinity;
        const e = +this.dataset.endYear || Infinity;
        const destroyed = this.dataset.endReason === 'destroyed' && year >= e;
        const unborn = year < s;
        if (unborn) {
            this.style.display = 'none';
        } else if (destroyed) {
            this.style.display = 'block';
            this.classList.add('destroyed');   // crosshatch + red X overlay via CSS
        } else {
            this.style.display = 'block';
            this.classList.remove('destroyed');
        }
    });
}
```

This is O(tagged entities) per year change, which is trivial (<1ms for a few hundred DOM nodes). No re-render, no re-layout, no data fetch.

`OnModeChanged` in [GalaxyMapUnified.razor:929](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor#L929) calls `applyTemporalBaseFilter(_year)` when entering Timeline and `clearTemporalBaseFilter()` when returning to Explore. `LoadYear` calls `applyTemporalBaseFilter(_year)` every scrub.

### Destroyed rendering treatment

Two visual states per entity type:

| Entity | Unborn (year < start) | Alive (start ≤ year ≤ end) | Destroyed (year > end) |
| --- | --- | --- | --- |
| Celestial body (planet) | Hidden | Normal | Faded + red crosshatch ring, tooltip "Destroyed: {year}" |
| Structure (Death Star, base) | Hidden | Normal glyph (icon varies by class) | Hidden (it's gone) or one-year debris marker |
| Trade route | Hidden | Normal dashed line | Hidden, or dimmed if `TemporalExplicit=false` (lower confidence) |

The "destroyed ring" for planets matters because players want to see *where* Alderaan was, not lose the dot entirely. For structures, hiding is fine — Death Star debris isn't a persistent landmark.

### Other temporal enhancements worth considering

These fit on top of the same temporal-annotation architecture without further rewrites:

1. **Capital pin.** Highlight the capital system of the dominant faction at the current year via a small crown icon. Data: `has_capital` edges with temporal bounds; e.g. Coruscant for Republic/Empire/New Republic, Hosnian Prime for the New Republic 29–34 ABY (then destroyed), Exegol for the Sith Eternal briefly. Single edge + glyph overlay — very cheap, high narrative payoff.
2. **Active war ribbons.** When a `War` node is active at the current year, draw a faint shaded band connecting its battle locations (the existing `Battle → took_place_at` edges). Lets users *see* the Clone Wars spread across the Outer Rim. Renders as a convex hull or loose band, not a precise overlay.
3. **Front-line shading between factions.** Cells adjacent to two contested factions get a hash-pattern tint. Computed from `territory.years` + grid adjacency. Already half-implemented (the `contested` flag exists); just needs a per-cell rather than per-region render. Cheap extension of the existing territory layer.
4. **Military presence heatmap (from 003 §6).** Lower-weight faction presence in cells where the faction fought battles that year, even without an `affiliated_with` edge. Already proposed in 003; rendering would reuse the event heatmap layer with a different colour ramp.
5. **Reset-to-present jump.** Single button in the timeline control that jumps `_year = max(AvailableYears)`. Cheap convenience.

Items 1, 3, and 5 are cheap and should probably land together with the core temporal-base filter. Items 2 and 4 are more expensive (new rendering layer, more ETL work) — tracked separately.

## Complexity and rewrite assessment

**What changes:**

| Surface | Extent | Risk |
| --- | --- | --- |
| `GalaxyGeography.cs` | +4 optional fields per DTO, new `GeoStructure` class | Low — additive |
| `GalaxyMapETLService.cs` | ~150 LOC added: temporal facet projection, structure discovery, trade-route bounds derivation | Medium — builds on existing adjacency list and facet handling |
| `MapService.cs` | Possibly a few LOC to stamp `data-*` attributes via API, or untouched if annotations ride on `GalaxyOverviewDocument` | Low |
| `GalaxyMapUnifiedController.cs` | No new endpoints; annotations ride on the existing `api/galaxy-map` overview response | None |
| `galaxy-map-unified.js` | +~80 LOC for `applyTemporalBaseFilter` / `clearTemporalBaseFilter`, +data attributes on draws | Medium — the D3 scene graph touches several draw paths |
| `GalaxyMapUnified.razor` | +2 JS calls in `OnModeChanged` and `LoadYear`. No UI additions | Low |

**What does NOT change:**

- MongoDB schemas for `kg.nodes` / `kg.edges` (already carry the facets).
- The two-mode toggle UI.
- Route/endpoint count (still one controller, one overview endpoint).
- `galaxy.years` year-document shape — existing territory/events pipeline is untouched.
- Explore mode behaviour.

**Rewrite required:** No. This is an **extension** of the Phase 8 ETL and a **new layer on top** of the existing D3 renderer. There is no redesign of the mode model, no schema migration, no controller reshuffling. The only non-trivial work is the D3-side filter pass and stamping `data-*` attributes at draw time.

## Alternatives considered

### Alternative 1 — Pre-bake per-year geography

Store a full `GalaxyGeography` snapshot per year inside each `GalaxyYearDocument`. Dumb client, fast scrub: year change → swap geography payload.

- **Cost:** ~100 years × ~1 MB geography ≈ 100 MB blob per rebuild. Each timeline scrub is an HTTP round trip + re-render of hundreds of SVG nodes.
- **Reject:** rules out instant scrubbing, bloats ETL, and duplicates data that's ≥99% identical across years. The user explicitly asked about simpler, less error-prone alternatives — this is the opposite.

### Alternative 2 — Server-side year-filtered endpoint

`GET api/galaxy-map/geography?year=4` returns a pre-filtered base layer.

- **Cost:** every scrub is a network round trip + full geography deserialize. Breaks the existing instant-scrub contract.
- **Reject:** mechanically simpler on the server but ruins UX.

### Alternative 3 — Two Blazor pages, one Explore and one Timeline

Split [GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor) into two pages with distinct routes. Timeline mode has its own page, its own data, its own JS.

- **Cost:** ~1400 lines of Razor + ~1400 lines of JS duplicated and drifted. Loses shared scene graph. Navigation between the two modes is no longer a toggle; it's a route change.
- **Reject:** the user called out error-proneness as a concern; this is the most error-prone option. The 2026-04-05 refactor already consolidated four controllers into one; splitting back out would reverse that.

### Alternative 4 — Client-side KG queries

Ship the KG to the client and let JS compute everything live.

- **Cost:** KG is a MongoDB-scale dataset, not browser-shippable.
- **Reject:** silly at this data size.

### Alternative 5 (chosen) — Sparse temporal annotations + client-side filter

Ship a small sparse annotation payload alongside the existing geography, stamp DOM attributes, filter client-side per year.

- **Pros:** one ETL touchpoint, one new JS function, no new endpoints, no new collections, instant scrubs, no geography duplication, graceful degradation (entities with no temporal facets are always visible in both modes).
- **Cons:** temporal-explicit trade route filtering is noisy until `001-temporal-facets.md`'s explicit flag lands.
- **Accept.**

## Phased rollout

| Phase | Scope | Depends on |
| --- | --- | --- |
| **A** | Model changes + ETL additions for destroyed celestial bodies (highest-impact subset: Alderaan, Jedha, Scarif, Hosnian Prime, Lah'mu, etc.). No frontend changes yet — verify data via MongoDB MCP on `starwars-dev`. | None |
| **B** | Frontend `applyTemporalBaseFilter` for celestial bodies only. Render destroyed planets with the crosshatch ring. Wire to `OnModeChanged` + `LoadYear`. | A |
| **C** | Structures (Death Stars, Starkiller, Echo Base, Starforge). New `GeoStructure` rendering path in D3 (small icon glyphs). | B |
| **D** | Trade route temporal filtering (derived bounds). Start with "hide when outside bounds"; add low-confidence dimming once `TemporalExplicit` is available. | B, 001-temporal-facets explicit-flag |
| **E** | Capital-pin glyph + reset-to-present button (cheap UX wins). | B |
| **F** | Optional: front-line shading, war ribbons. Larger rendering work — separate design review. | C |

Phase A+B alone delivers the headline user-visible win ("Alderaan is gone in 4 ABY") and validates the end-to-end pipeline. Phases C–E are incremental and can ship independently.

## Open questions

1. **Destroyed-but-still-visible vs. fully-hidden planets.** Alderaan: keep the dot with destruction marker. Hosnian Prime: same? Jedha: partially destroyed, not fully — canon is ambiguous. Suggest: keep visible with destruction marker unless the infobox explicitly says "obliterated" / "rendered uninhabitable" and there's no physical body left.
2. **Lifespan span for nebulas and natural phenomena.** Treat as atemporal? Same question for the Maw, Kessel Run, etc. Default: atemporal.
3. **Continuity-dependent temporal bounds.** A planet might be destroyed in Legends but alive in Canon (or vice versa). Temporal facets already carry continuity via the parent `GraphNode.Continuity` — the filter must respect the active continuity filter (already in `GlobalFilterService`).
4. **"Present day" for open-ended routes/structures.** A route with `StartYear=1000 BBY, EndYear=null` should render at all years ≥ -1000. A destroyed planet with `EndYear=34 ABY` should vanish at year 35+. Covered by the null-handling in the filter pseudocode above; just calling it out.
5. **How to surface "this was here once" in Explore mode.** The user said Explore = everything static. If a planet is destroyed in-universe, does the Explore tooltip mention it? Suggest: yes, as a one-liner ("Destroyed in 0 BBY"), because Explore is the "full reference" view and hiding the fact would be weird. The *rendering* stays full-visibility; only the tooltip carries the detail.
