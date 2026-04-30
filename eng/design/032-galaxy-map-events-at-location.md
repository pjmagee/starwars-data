# Design-032: Events at Location (Galaxy Map Explore Mode)

**Status:** Proposal
**Date:** 2026-04-30
**Author:** Patrick Magee + Claude
**Related:** [Design-004 Galaxy Map Architecture](004-galaxy-map-architecture.md), [Design-030 Citation Link Resolver](030-citation-link-resolver.md), [Design-031 Galaxy Map Deep-Link Route](031-galaxy-map-deep-link-route.md)

## Problem

Design-031 added `/galaxy-map/{pageId}` for spatial entities. Combined with
the new "Galaxy Map" button on KG edge rows, a user reading the Battle of
Yavin's edges can click `took place at → Yavin 4` and land on Yavin 4 in
the map. That works — but the battle is no longer the subject. The side
panel shows the planet's properties; the battle is gone from view.

Today's split between modes:

| Mode     | Spatial subject | Events surfaced                                              |
| -------- | --------------- | ------------------------------------------------------------ |
| Explore  | Yes             | None — properties only                                       |
| Timeline | Yes             | Yes — but scoped to a single year, scattered across the map  |

So:

- **Explore is event-blind.** A user who knows "Yavin 4" and wants
  "everything that ever happened there" has no view for it.
- **Timeline is year-locked.** A user who knows "Battle of Yavin" but
  doesn't know it happened in 0 BBY/ABY can't get there from the map.
- **Indirect deep-links lose context.** `/galaxy-map/{locationId}` from a
  battle's edge row drops the battle. There's no way to keep it
  highlighted because no view of the map lists events at all in Explore.

This is the round-trip the user wants:

```text
KG · Battle of Yavin
  └─ took place at → Yavin 4 [Galaxy Map button]
                              │
                              ▼
Galaxy Map · Yavin 4
  ├─ system view, body selected
  └─ Events at this location
       ├─ Battle of Yavin              ← highlighted from query param
       ├─ Evacuation of Yavin 4
       ├─ Archaeological dig
       └─ … (sortable / filterable)
```

## Goals

- A new "Events at this location" section in the Explore-mode side panel
  for any focused spatial entity (System, CelestialBody, Sector, Region,
  TradeRoute).
- **Date-agnostic** — the section lists every event ever associated with
  the location, not scoped to a year. Timeline mode keeps its year-scoped
  role unchanged.
- **Deep-linkable** — `/galaxy-map/{locationId}?event={eventId}` opens the
  side panel with the events list expanded and the named event scrolled
  into view + visually highlighted. KG indirect-spatial links and the
  Citation Link Resolver (Design-030) can target this URL.
- **Filterable** by event category (Battle / Mission / Campaign / Siege /
  …) and continuity, sortable by date or title.
- **Cheap to query** — backed by `kg.edges` with the existing event→
  location labels, no new collections.
- **Bounded** — large locations (Coruscant, Tatooine) cap initial render at
  ~20 entries with a "show more" affordance, so the panel doesn't blow out.

## Non-goals

- **Replacing Timeline mode.** Timeline still renders events on the *map*
  for a single year, with the panning/zoom heatmap. Explore's events
  section is a flat textual list pinned to one location.
- **Rendering events on the map in Explore.** The list lives only in the
  side panel. Drawing battle markers across the system view is a separate
  visualisation question (out of scope here).
- **Transitive events** ("show me everything that ever happened in the
  Outer Rim Territories" when standing on Yavin 4). Phase 1 is direct edges
  only — `event → this location`. Region/Sector roll-ups are a follow-up,
  see Open Questions.
- **Editing events from the map.** The list is read-only; clicking an
  event opens the existing entity-detail flow (Wiki / KG / Timeline link
  options), it doesn't author anything.
- **Backfilling historical events not in the KG.** If `kg.edges` doesn't
  have an `event → location` edge, the event won't appear. Improving edge
  coverage is the ETL pipeline's job, not this UI.

## Approach

### Data — what counts as "an event happened here"

The set of edge labels that qualify as "an event happened at this
location" comes from the deterministic infobox graph builder:

```text
happened_at, took_place_at, happened_in, hosted_battle,
fought_at, located_at, occurred_at, site_of, conducted_at
```

Source side is any node whose `Type` is in the **Event family**: Battle,
Mission, Campaign, Siege, War, Conflict, Attack, Operation, Event,
Skirmish, Encounter. The exact label list lives in a single
`SpatialEventLabels` constant (per ADR-002 — type-and-label catalogues
go in the model layer, not scattered in queries) so backend, frontend,
and the citation resolver share it.

### Data source — `kg.edges` vs `kg.edges.bidir` vs `kg.edge_enrichments`

There are three collections in play, with different roles:

| Collection            | Built by                        | Contains                                                                                                                       |
| --------------------- | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| `kg.edges`            | `InfoboxGraphService` (Phase 6) | Authoritative deterministic edges with `reverseLabel` denormalized in.                                                         |
| `kg.edges.bidir`      | Mongo view over `kg.edges`      | Forward + reverse copies of every edge — cheap "outgoing-from-X" abstraction without a C# flip.                                |
| `kg.edge_enrichments` | Holocron consolidator           | Agent-added Add/Annotate/FillGap edges. Promoted into `kg.edges` only when consolidated; otherwise live as a separate overlay. |

**Phase 1 reads `kg.edges` directly, flipping direction in C# using
`FieldSemantics.Relationships`** — the exact pattern
`KnowledgeGraphQueryService.GetAllEdgesForEntityAsync` already uses (KG
page edges table, copilot tool, AskAI tool). Reasons:

- *Consistency.* Switching to `kg.edges.bidir` for one query while every
  other read path ignores the view splits the abstraction. If we move to
  the view, we should move *everything* — that's a separate refactor with
  its own risk surface (the view doesn't carry every field used by the
  bound-provenance and EdgeMeta projections, see Design-021).
- *Cost.* The query is `{toId: locationId, label: {$in: [...]}}` with an
  index on `(toId, label)` — sub-ms.
- *Provenance.* Edges in `kg.edges` carry `meta.boundsSource` (Design-021)
  so we can tag rows as "Infobox" / "Holocron" / "Lifecycle" in the UI
  without a separate join.

**Phase 1.5 — overlay `kg.edge_enrichments`** for spatial labels that
were proposed by Holocron but haven't been consolidated yet. These should
appear in the list with an "agent-derived" badge, the same affordance the
node-properties section uses for `kg.enrichments`. Implementation:
small `$unionWith` from `kg.edge_enrichments` filtered by status =
"active" and matching label set. Sort merges naturally on year.

> Why not just always read from `kg.edges.bidir`? The bidir view doesn't
> include `kg.edge_enrichments`, so it would still need the same overlay
> step. And it doesn't help us avoid the per-row tagging needed for the
> "agent-derived" badge. So the view buys us nothing here.

**Phase 2 — single physical materialised view** if the overlay union
becomes a hot path. Build `kg.edges.with_enrichments` once during the
ETL and read from it directly. Out of scope for this doc; flag if the
P95 of the location-events endpoint regresses.

### Backend — `GET /api/galaxy-map/locations/{pageId}/events`

Returns the list of events that took place at the location, sorted by
sort-key year ascending (BBY → ABY), nullable years last:

```json
{
  "locationId": 453302,
  "locationName": "Yavin 4",
  "totalEvents": 47,
  "events": [
    {
      "pageId": 24911,
      "name": "Battle of Aargonar (Galactic Civil War)",
      "type": "Battle",
      "category": "Battle",
      "continuity": "Canon",
      "year": 0,
      "yearDisplay": "0 BBY/ABY",
      "wikiUrl": "https://starwars.fandom.com/wiki/Battle_of_Aargonar",
      "edgeLabel": "happened_at"
    },
    ...
  ]
}
```

- Implemented as a new `EventsAtLocationService` next to `MapService`.
- Single aggregation against `kg.edges` (filtered by label set + target
  pageId) joined to `kg.nodes` for source name/type/year — same pattern as
  `KnowledgeGraphQueryService.GetAllEdgesForEntityAsync`.
- Honours the request-scoped continuity from `ICurrentRequestContext`
  (ADR-008 + Design-029) — Canon-only mode hides Legends events.
- Page-cached via `[ResponseCache]` keyed on pageId + continuity since the
  underlying graph is read-only at runtime.

### Frontend — side panel section

In `GalaxyMapUnified.razor`'s detail panel, after the existing properties
section, render:

```razor
<MudExpansionPanel @bind-IsExpanded="_eventsExpanded"
                   HideIcon="false">
  <TitleContent>
    <MudText>Events at this location</MudText>
    <MudChip T="string" Size="Size.Small" Class="ml-2">@_events.Count</MudChip>
  </TitleContent>
  <ChildContent>
    <!-- category + continuity filters -->
    <!-- sortable list, ~20 visible, "show more" button -->
    <!-- each row: name, year, category chip, link options -->
  </ChildContent>
</MudExpansionPanel>
```

Auto-fetch on entity selection (existing `LoadPageDetail` is the natural
home — it already runs when the user picks a body from the system view).
Cache per-pageId in component state so re-selection is instant.

When `?event={eventId}` is present in the URL, expand the panel and call
`scrollIntoView` on the matching row, then apply a 2-second highlight
flash (existing CSS class on the timeline page, reuse).

### Deep-link plumbing

`GalaxyMapUnified.razor`:

```csharp
[Parameter, SupplyParameterFromQuery] public int? Event { get; set; }
```

In `TryApplyDeepLinkAsync`, after the JS module finishes drilling, call a
new `HighlightEventAsync(Event.Value)` that:

1. Ensures `_events` is loaded for the focused entity.
2. Sets `_eventsExpanded = true`.
3. Awaits `JS.InvokeVoidAsync("scrollEventIntoView", eventId)`.

The Citation Link Resolver (Design-030) emits `/galaxy-map/{locId}?event=
{eventId}` when its source node is in the Event family and has a
spatial-target edge. KG edge rows with type Event + spatial target row
linkify the same way.

### KG edge row link upgrade

The button added on the edges table today links to
`/galaxy-map/{spatialTargetId}`. When the *source* node (the one whose
edges we're viewing) is in the Event family, append the source's pageId
as `?event=` so the round-trip works:

```razor
@if (IsSpatialKind(edge.OtherType))
{
    <MudIconButton Icon="@Icons.Material.Filled.Explore"
                   Href="@BuildGalaxyMapHref(edge.OtherId, _expandedNode)"
                   Target="_blank" />
}

string BuildGalaxyMapHref(int locationId, GraphNode source) =>
    IsEventKind(source.Type)
        ? $"/galaxy-map/{locationId}?event={source.PageId}"
        : $"/galaxy-map/{locationId}";
```

## Implementation order

1. **Backend** — `SpatialEventLabels` constant, `EventsAtLocationService`,
   `LocationsController.GetEventsAt(pageId)`. Unit + integration tests
   against a seeded `kg.edges` fixture.
2. **Frontend list** — side panel section, fetch on selection, no
   filters/sort yet. Verify on Yavin 4 / Tatooine / Coruscant manually.
3. **Sort + category filter** — once the list is too long to scan.
4. **Deep-link `?event=`** — extend `TryApplyDeepLinkAsync`, add
   `scrollEventIntoView` JS helper. Verify `/galaxy-map/453302?event=
   452305` (Yavin 4 highlighting Battle of Yavin).
5. **KG edge row link upgrade** — `BuildGalaxyMapHref` swap-in. Verify
   from Battle of Yavin's edge to Yavin 4 keeps the battle highlighted.
6. **Citation Link Resolver wiring** — Design-030 emits the augmented URL
   for Event-family citations.

## Open questions

- **Region / Sector roll-up.** Should standing on a Sector list events
  that happened on any system *within* the sector? Yes for sectors and
  regions probably — the user is looking at "Outer Rim" expecting wars
  fought there. Implementation is a one-hop graph walk (location → has-
  system → events) that gets expensive on large regions. Phase 1 ships
  *direct edges only*. Phase 2 adds a roll-up toggle.
- **Trade routes.** Events that happened *along* a route (skirmishes,
  patrols) are an edge type we don't currently model. Defer to ETL.
- **Per-faction filter.** "Show only Empire-side events at Yavin 4."
  Possible via `belligerent` edge join, but adds query cost. Leave for
  later iteration.
- **Cap behaviour.** When an event happened at the Coruscant level, the
  list is hundreds. Show "20 of 312, show more" or paginate? Probably
  initial 20 + on-demand load.
- **Real-world publication events.** Books and episodes have their own
  `setting` edges that resolve to spatial nodes. Should they appear in
  this list? They sit alongside the global Realm filter (Star Wars vs
  Real World) — when Real World is on, include them; when Star Wars
  only, exclude. Same envelope as continuity.

## Risks

- **Duplicate edges.** Some events have both `happened_at` and
  `took_place_at` to the same location (different infobox sources).
  Group on `(eventPageId)` in the aggregation so the row appears once
  with the most-specific label.
- **Event-family classification drift.** New entity types added by future
  ETL changes might be event-like but not in our enumerated list. Make
  the list a registered-once constant in `Models/SpatialEventLabels.cs`
  with an annotated source so Holocron / Phase-2 builders know to update
  it together.
- **URL share-link UX.** A shared `?event=…` URL needs the receiver to
  see the highlighted event even on a slow connection. The deep-link
  flow already retries until the JS module is ready (Design-031), so
  the same pattern applies to the events section.
