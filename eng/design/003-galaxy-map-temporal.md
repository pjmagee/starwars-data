# Design: Galaxy Map ETL — Temporal Knowledge Graph Integration

**Status:** Draft
**Date:** 2026-04-03

## Problem

The Galaxy Map ETL (`GalaxyMapETLService`) reads from `kg.nodes` and `kg.edges` but ignores `TemporalFacets` and edge temporal bounds (`fromYear`/`toYear`). This causes:

1. **Multi-year events truncated** — A war spanning 22-19 BBY only appears in year 22. Viewers can't see conflicts unfold across the galaxy map timeline.
2. **Territory control ignores edge temporality** — A planet affiliated with the Empire in years 19 BBY–5 ABY gets counted as Empire-controlled at all years, even after the Empire fell.
3. **No semantic distinction** — Character births, battles, government foundings, and book releases are all treated identically as single-year events.

## Improvements

### 1. Multi-Year Events from TemporalFacets

Currently: `eventsByYear[node.StartYear] = node` — one year per event.

Fix: Use `conflict.start`/`conflict.end` facets to span events across their full duration:
- Wars, Campaigns: appear in every year of their range
- Battles, Missions: single year (conflict.point)
- Governments: appear across their institutional lifecycle

### 2. Territory Control Respects Edge Temporal Bounds

Currently: affiliation edges loaded without temporal filtering — a planet affiliated at any time counts forever.

Fix: When computing control for a specific year, only count `affiliated_with` edges where `fromYear <= year <= toYear` (or where temporal bounds are null, meaning always active).

### 3. Semantic Event Type Handling

Use `TemporalFacets[].Semantic` prefix to determine behavior:
- `conflict.*` → span the full date range on the map
- `lifespan.*` → single year at birth/death
- `institutional.*` → span the lifecycle, show reorganizations as distinct events
- `publication.*` → skip (real-world dates, not in-universe)

### 4. Government Succession at Year Level

Currently: only checks `startYear <= year <= endYear` for government existence.

Fix: Use `institutional.reorganized`, `institutional.fragmented`, `institutional.restored` facets to show government state transitions within the timeline.

## Post-Refactor Opportunities (Added 2026-04-04)

After the KG refactor — infobox parser fix, per-template field scoping, temporal-edge fix, `FieldSemantics` + `DefaultLabelSelector`, and the 2026-04-04 KG rebuild — several new opportunities appear. These sit on top of the original design above and should be evaluated together.

### 5. Calendar-Scoped Event Inclusion

The ETL currently includes any node with a non-null `StartYear` in the event timeline, including Person births, Book publication dates, Movie releases, etc. These are real-world CE years (e.g. `1977`, `2015`) and get treated as galactic years, polluting the timeline.

`TemporalFacets[].Calendar` now carries `"galactic"`, `"real"`, or `"unknown"`. The event loader should filter to facets whose calendar is `"galactic"` (or where the envelope clearly falls in the galactic range, as a safety net). Publication / lifespan-real facets should never appear on the galaxy map.

Concrete fix in `GalaxyMapETLService.BuildAsync` step 5:

```csharp
var primaryFacet = node.TemporalFacets.FirstOrDefault();
if (primaryFacet is not null && primaryFacet.Calendar == "real")
    continue; // skip publication/real-world dates entirely
```

### 6. Battle-Derived Territorial Presence

The rebuilt KG now contains:

- **13,029 `belligerent` edges** (Battle → faction)
- **17,505 `commanded_by` edges** (Battle → commander)
- **6,569 `participants` edges** (Mission → character)
- Battle nodes with `conflict.point` temporal facets linked via `took_place_at` to planets

The current faction-region attribution only uses `affiliated_with` edges, which miss cases where a faction was **militarily active** in a region without a formal affiliation. Example: The Empire fought battles across the Outer Rim throughout the Clone Wars and Galactic Civil War; some of those planets never had a formal `affiliated_with` edge to the Empire yet the Empire clearly had territorial presence there during those years.

Proposed additional pass:

```text
For each Battle node with a Date facet:
  faction(s) = belligerent edges from this battle
  location = took_place_at edge → planet → region (via planetToRegion)
  year = Date facet's year
  → add (faction, region, year, year) to temporalAffiliations with lower weight
```

Weight these battle-derived presences lower than formal affiliations in `ComputeRegionControls` so they don't override confirmed sovereignty, but they fill gaps in conflict zones.

### 7. Institutional Lifecycle Transitions

The flat `startYear`/`endYear` envelope on Government nodes collapses the rich institutional lifecycle. The Galactic Empire has 7 facets:

```text
institutional.start      19 BBY  (established)
institutional.fragmented 4 ABY   (Imperial Remnants)
institutional.end        5 ABY   (dissolved)
institutional.reorganized 5 ABY  (rump state in inner systems)
institutional.reorganized 9 ABY  (neo-Imperial forces)
institutional.restored   21 ABY  (as the First Order)
institutional.restored   35 ABY  (as the Final Order)
```

The current ETL treats this as one continuous Empire from 19 BBY to 35 ABY. That's wrong between 5–21 ABY — the Empire was dissolved/fragmented, and the map should show the territory differently (New Republic ascendant, Imperial Remnants pocketed in specific sectors).

Fix: `ComputeRegionControls` should use the per-year institutional state, not just the envelope:

- During `fragmented → restored` gaps → treat as inactive or as its successor entity
- Show `restored` transitions as the restored form (First Order, Final Order) not the original name

This is a non-trivial modelling change because it requires splitting one Government node into multiple "active phases" at query time, or pre-computing a year-by-year state map during ETL.

### 8. Conflict.start/end for Multi-Year Span, Replacing the `spanYears` Heuristic

Current code (step 5) uses a heuristic to decide whether to span a node across multiple years:

```csharp
var spanYears =
    semanticPrefix is "conflict" or "institutional"
    && endYear > startYear
    && (endYear - startYear) <= 100;
```

This reads only the **first** facet's prefix. With richer facets, we can be precise:

- If a node has both `conflict.start` and `conflict.end` facets, span those exact years
- If it has `conflict.point` only, treat as single year
- If it has `institutional.start` and any of `institutional.end`/`fragmented`/`reorganized`, use those transitions as segment boundaries

This makes wars like the Clone Wars appear exactly 22 BBY → 19 BBY rather than relying on a first-facet guess.

### 9. Schema Change Dependency: Explicit vs Derived Edge Temporal Bounds

See the companion note in [001-temporal-facets.md](001-temporal-facets.md#to-revisit-explicit-vs-derived-edge-temporal-bounds). Half of current edges have temporal bounds but most are **derived** from source/target lifespan overlap — a necessarily-true envelope, not the actual relationship window. Before #6 can be trusted for fine-grained territorial inference, `RelationshipEdge` should grow a `TemporalExplicit` flag so the ETL can prefer explicit bounds for the battle-derived presence pass.

### 10. Two-Phase Rollout

The improvements above have different effort/value profiles. Suggested ordering:

| Phase | Scope | Effort | Dependencies |
| --- | --- | --- | --- |
| **A** | Plain rebuild of the galaxy map against the cleaned KG (no code changes) | Trivial | None |
| **B** | Add calendar filter (#5) + conflict.start/end spanning (#8) | Small | None |
| **C** | Battle-derived presence (#6) with lower weight | Medium | Implies #5 is in place to avoid double-counting |
| **D** | Institutional lifecycle transitions (#7) | Large | Needs modelling decision about successor entities (First Order ↔ Galactic Empire) |
| **E** | Use `TemporalExplicit` flag for high-confidence territorial inference (#6 refinement) | Medium | Depends on schema change in 001-temporal-facets.md |

Phase A captures most of the immediate value (cleaner data, richer battle edges, fixed infobox parser). Phases B/C/D are iterative improvements that can be designed and shipped independently.
