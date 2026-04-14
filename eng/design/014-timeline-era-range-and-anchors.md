# Design 014: Timeline Era Range Filter & KG Temporal Anchors

**Status:** Phase 1 Shipped 2026-04-14 (From/To era selects). Phase 2 Draft (KG temporal anchors).
**Date:** 2026-04-14

## Context

The timeline page ([Timeline.razor](../../src/StarWarsData.Frontend/Components/Pages/Timeline.razor)) uses a `MudChipSet` in [EventTimeline.razor:60](../../src/StarWarsData.Frontend/Components/Shared/EventTimeline.razor#L60) to let users multi-select eras. Each chip maps to an `Era` record returned from `Timeline/eras` (name + `StartYear/EndYear/Demarcation`). When one or more chips are selected, the frontend compresses them into a single `(YearFrom..YearTo)` range via `GetEraYearRange()` and passes it to the API.

Two usability gaps:

1. **Contiguous-range picking is tedious.** The common case — "show me everything from the Old Republic era through the Rise of the Empire" — needs 5+ individual chip clicks.
2. **Users occasionally want to anchor the range on a *named historical event or entity* rather than an era.** "Show me events during the Clone Wars." "Show me what happened while the Galactic Empire existed." Today there's no way to express that short of knowing the year boundaries by heart.

## Considered: CodeBeam `MudRangeSlider`

The user proposed `MudRangeSlider` from `CodeBeam.MudBlazor.Extensions`. Rejected for three reasons:

1. **New third-party dependency** on top of MudBlazor. Per [CLAUDE.md](../../CLAUDE.md) ("Library Deviations") + [ADR-004](../adr/004-mudblazor-deviations.md), adding a library requires an ADR justification, and the value has to exceed "standard MudBlazor components can't do this".
2. **Eras are named, non-uniform spans.** "Old Republic era" covers ~25,000 years; "Rise of the Empire" covers ~32. On a linear year-axis slider the early eras collapse to invisible slivers and recent eras dominate. A *categorical* slider (indices 0..N-1) would work but isn't `MudRangeSlider`'s sweet spot.
3. **A range slider can't express non-contiguous multi-select.** It replaces the chip set's capabilities rather than extending them.

## Decision

**Two-layer filter, zero new dependencies.**

The `MudChipSet` remains the source of truth. Two MudBlazor-native extensions layer on top:

- **Phase 1 (shipped):** Two `MudSelect<string>` controls labelled "From era" / "To era". Picking values expands to a contiguous chip selection covering every era in `[from..to]` inclusive. Manually editing chips syncs back — if the result is contiguous the From/To selects reflect the endpoints; otherwise they blank and a "Custom" pill surfaces that state.
- **Phase 2 (draft):** A `MudAutocomplete<KgTemporalNode>` ("Filter by event / lifetime…") that searches `kg.nodes` for anything with a `TemporalFacet`. Picking "Clone Wars" sets `YearFrom = 22 BBY, YearTo = 19 BBY` directly. Picking "Galactic Empire" sets `19 BBY → 4 ABY`. The node-derived range composes with (intersects) the era range.

Both layers write into the same `(YearFrom, YearTo)` query params the API already accepts — no backend change for Phase 1.

## Phase 1: From/To era selects (shipped)

### UX

```
Filter by Era
[From era ▾]  →  [To era ▾]  [Clear]  (Custom)?
─────────────────────────────────────
[ ] Old Republic era (25,000 BBY – 1,000 BBY)
[ ] New Sith Wars (2,000 BBY – 1,000 BBY)
[ ] Rise of the Empire era (1,000 BBY – 19 BBY)
...
```

- From and To populate from the same era list (sorted by linear year via `ToLinear`).
- Either can be left blank: blank From = start of list; blank To = end of list.
- "Clear" wipes both selects and the chip selection.
- "Custom" chip appears only when the chip selection is non-contiguous (i.e. user hand-picked eras the selects can't express).

### Sync model

One-way From/To → chips is authoritative when the user touches a select. Chip edits sync back:

```
ApplyFromToRange()       — From/To change → rebuild _selectedEraNames as [from..to]
OnEraChipsChanged()      — chip toggle → update _selectedEraNames, then SyncFromToFromChips()
SyncFromToFromChips()    — contiguous run? reflect endpoints : blank both
IsContiguousSelection()  — drives the "Custom" pill
```

Edge case handled: continuity switch (Canon ↔ Legends) drops some era names. We filter `_selectedEraNames` to the new visible set (existing behaviour), then null any From/To pointing at a dropped era, then `SyncFromToFromChips()` to re-derive.

### Why not a single select-with-multiselect?

A `MudSelect` with `MultiSelection=true` would be equivalent to the chip set (already there) — it doesn't solve the contiguous-range-in-one-step problem. The From/To pair is exactly the "I want everything between A and B" metaphor, which is the dominant browsing pattern.

## Phase 2: Anchored timeline route `/timeline/{nodeId}`

### Proposal

Promote "timeline during X" from a query-param overlay to a **first-class page** at `/timeline/{nodeId}`. Bookmarkable, shareable, SEO-friendly, and — crucially — able to expose the node's **multiple semantic dimensions** through a dedicated picker rather than silently flattening them.

**Route:**

- `/timeline` — unchanged, the generic browsing experience with era From/To + chips (Phase 1).
- `/timeline/{nodeId}` — new. The "timeline during \<node\>" page. Reuses the `EventTimeline` component underneath, adds chrome above.

### Why a dedicated route beats a query param

The earlier sketch (`/timeline?anchor=<id>`) reused the generic page and added an anchor chip above the era filter. Two things that approach failed:

1. **Multi-semantic nodes get silently flattened.** A `TemporalFacet` carries up to 6 semantic dimensions. A character like **Palpatine** has *at least* four defensible ranges: full lifetime (82 BBY – 4 ABY), Senate career, Sith apprenticeship, reign as Emperor. A single `(YearFrom, YearTo)` anchor forces the frontend to pick one and hide the rest. Users then can't tell *which* one is active, and can't switch.
2. **It's not a real page.** No `PageTitle`, no meta description, no natural bookmarking. Deep-links from other pages (GraphExplorer, Search, KG nodes) don't produce a durable entry point in the user's mental model.

The route approach fixes both: the URL *is* the state, and the semantic picker surfaces all candidate ranges explicitly.

### Page layout

```
← Back to Palpatine                                           (MudButton Text, Href = node source page)
──────────────────────────────────────────────────────────
Timeline during Palpatine                                     (MudText Typo=h4)
──────────────────────────────────────────────────────────
[ Lifetime ] [●Senate career ] [ Reign ] [ Custom ]           (MudToggleGroup — one active)
52 BBY – 19 BBY · Galactic calendar · Canon                   (read-only header chip)
──────────────────────────────────────────────────────────
[ existing EventTimeline:
    From/To era selects + chip set (Phase 1),
    calendar tabs, category filter, results list ]
```

Nodes with **one** semantic dimension skip the `MudToggleGroup` entirely — just render the read-only header: `22 BBY – 19 BBY · Galactic calendar · Canon`. No friction for the Clone Wars case.

### Semantic dimension mapping

`TemporalFacet` surfaces dimensions per node type. v1 mappings:

| Node type     | Default dimension     | Other offered dimensions                                                   |
| ------------- | --------------------- | -------------------------------------------------------------------------- |
| Conflict      | Hostilities period    | — (single-range, no picker)                                                |
| Government    | Reign / existence     | — (single-range, no picker)                                                |
| Organisation  | Active period         | Founded → dissolved (if distinct)                                          |
| Character     | Active period         | Lifetime, Reign / office (per role), Apprenticeship                        |
| Treaty / Law  | In-force period       | Ratification day (instant → ±1 day window)                                 |
| Era           | —                     | Redirects to `/timeline` with era pre-selected (no anchored page)          |

Default-selection priority: **Reign / office > Active period > Lifetime**. Rationale: "most specific that covers when this person mattered" is a better default than raw birth-to-death.

### Composition with the era filter

The anchor range is a **ceiling**, not a replacement:

- The inner `EventTimeline` is told "you may only show results inside `[anchor.YearFrom, anchor.YearTo]`". Era From/To and chips still work *inside* that window.
- Picking an era that falls entirely outside the anchor yields an empty result — correct feedback, not a bug.
- **"Custom"** in the semantic picker clears the ceiling and hands full control back to the era filter (page becomes equivalent to plain `/timeline` with a back-link to the node).

### URL structure and 404 behaviour

- `/timeline/{nodeId}` where `{nodeId}` is the `kg.nodes._id` (same id scheme as GraphExplorer's `/graph-explorer/{PageId}` — consistent with the existing pattern).
- Optional `?semantic=<dim>` query param preselects a non-default dimension. Omitted = use the priority rule above. Invalid values are ignored (fallback to default, don't 404).
- **Nodes without any `TemporalFacet` → hard 404**, not a silent fallback to `/timeline`. Landing on a temporal timeline for a lightsaber is a broken promise; fail loud so callers fix their links.
- **Era-type nodes redirect** to `/timeline?era=<name>` (Phase 1's chip-preselected state, once that URL state is added) to avoid two different routes expressing the same thing.

### Entry points

- **GraphExplorer focused node** — when `node.HasTemporalFacet`, show a secondary `MudButton` "View timeline during \<name\>" next to the existing actions.
- **Knowledge Graph node cards** — same button in the node detail drawer.
- **Search results** — follow-up. When a search hit is a temporal node, add an inline "Timeline" chip on the card alongside the primary "Open" action.
- **Direct link** — users type or bookmark `/timeline/clone_wars`. Works without any UI plumbing.

Each entry point is additive; the button only appears on nodes that support it, so there are no dead affordances.

### API

Two endpoints:

1. `GET /api/Timeline/anchor/{nodeId}?continuity=<c>` — returns the node's name, type, continuity-scoped `TemporalFacet`, and the list of available semantic dimensions with their year ranges. Drives the page header and picker.

   ```json
   {
     "id": "palpatine",
     "name": "Palpatine",
     "type": "Character",
     "continuity": "Canon",
     "sourceHref": "/wiki/Palpatine",
     "dimensions": [
       { "key": "lifetime",        "label": "Lifetime",      "yearFrom": 82, "fromDemarcation": "Bby", "yearTo":  4, "toDemarcation": "Aby" },
       { "key": "senate_career",   "label": "Senate career", "yearFrom": 52, "fromDemarcation": "Bby", "yearTo": 19, "toDemarcation": "Bby" },
       { "key": "reign",           "label": "Reign",         "yearFrom": 19, "fromDemarcation": "Bby", "yearTo":  4, "toDemarcation": "Aby" }
     ],
     "defaultDimension": "senate_career"
   }
   ```

2. Existing `GET /Timeline/events?...` — unchanged. The anchored page just clamps its `YearFrom/YearTo` query params to the active dimension before calling it.

No new Mongo indexes needed; `kg.nodes._id` lookup is already covered.

### Open questions

1. **Composition vs override — "Custom" mode.** When the user picks "Custom", do we keep them on `/timeline/{nodeId}` with the ceiling removed, or navigate them to `/timeline` proper? Navigating is cleaner (URL reflects state); staying preserves the back-link. Lean *navigate*, add a breadcrumb-style "Coming from: Palpatine" link on `/timeline` that survives for the session.
2. **Canon vs Legends divergence.** A node's temporal facet differs across continuities. The page respects the global continuity filter, but if the user flips continuity while on `/timeline/clone_wars`, do we refetch the anchor (dimensions may differ) or require a full navigation? Lean *refetch silently*.
3. **"Battle of Geonosis" (single-day node).** Degenerate range — one year in, same year out. Show a warning chip ("This is a single-day event; results may be sparse")? Or trust the user?
4. **Dimension discovery.** Users won't know which dimensions a node offers until they land on the page. Acceptable for v1; could be surfaced in GraphExplorer's button tooltip in v2.

## Non-goals

- **Slider-based picking.** Rejected above.
- **Arbitrary date-range inputs** (user types "from 200 BBY to 50 BBY"). The user base is not fluent enough in galactic years for freeform input to beat named anchors; skip.
- **Multi-anchor composition** (e.g. "during Clone Wars OR during Galactic Civil War"). Phase 2 is single-anchor; revisit if a real use case surfaces.

## Revisit when

- MudBlazor core ships a range slider: re-evaluate whether a categorical range slider replaces the From/To pair on mobile where vertical real estate is scarce.
- Character "active period" facet lands: add `Character` to the Phase 2 autocomplete's allowed types.
- Phase 1 usage data shows users routinely selecting non-contiguous chip combinations: drop "Custom" in favour of a tagged, named preset mechanism.
