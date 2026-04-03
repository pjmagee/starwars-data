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
