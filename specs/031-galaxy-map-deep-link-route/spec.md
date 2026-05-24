# Design-031: Galaxy Map Deep-Link Route

**Status:** Implemented (Phases 1–3)
**Date:** 2026-04-30
**Author:** Patrick Magee + Claude
**Related:** [Design-004 Galaxy Map Architecture](../004-galaxy-map-architecture/spec.md), [Design-030 Citation Link Resolver](../030-citation-link-resolver/spec.md), [Design-022 Page-Aware Copilot Sidebar](../022-galaxy-map-copilot/spec.md)

> **Shipped 2026-05-18.** Phase 1 (System/CelestialBody via `drillToDeepLink`,
> Region overlay, **Sector** via the new `drillToSector` JS verb) and Phase 2
> (**TradeRoute** via `highlightTradeRouteById` — 3× pulse, stays at overview)
> are live. Verified against `starwars-dev` in-browser: Yavin 4 (CelestialBody),
> Gordian Reach (Sector → breadcrumb + systems + copilot context), Corellian
> Run (TradeRoute → highlight snackbar). **Phase 3** (Citation Resolver
> wiring) completed with Design-030 Phases 2–4 on 2026-05-18 — every spatial
> citation (direct or via an indirect KG hop) now hands the user a
> `/galaxy-map/{id}` button, and Event-family sources carry `?event=` so the
> route lands on the location with the event highlighted.

## Problem

The galaxy map (`/galaxy-map`) has no URL-based deep-link surface. Today the
only way to navigate to a specific entity on the map is to open the page,
search/click through the breadcrumb, or wait for the AI agent to interact
with the JS module via JSInterop. There's no way to:

- Share a URL that points to a specific planet, system, sector, region, or
  trade route.
- Have the copilot or AskAI agent emit a citation link that lands the user
  on the map zoomed and selected to the right entity.
- Open a galaxy-map view from a Wookieepedia-style article click in another
  part of the site.

Design-030 (Citation Link Resolver) wants a `/galaxy-map/{pageId}` link in
its option set for spatial entities. That link doesn't exist yet — this
design adds it.

## Goals

- A single `/galaxy-map/{pageId:int}` route that resolves the pageId against
  `kg.nodes` and drives the existing JS module to the correct view + selection.
- Type-aware drill: planets/celestial bodies open the system view with the
  body selected; systems open the system view; sectors land on the sector
  breadcrumb level; regions land on the region overlay; trade routes
  highlight the route on the map.
- Graceful failure: a pageId that's not spatial (Character, Battle, etc.)
  or doesn't exist falls back to the overview with a toast.
- No new geography data — the existing `galaxy.geography` overview, system
  cache, and JS module handle all interaction; the route just orchestrates.
- Works for both authenticated and anonymous users; no auth-gated content
  on the map.

## Non-goals

- **Indirect spatial deep-linking** ("show me where the Battle of Coruscant
  was fought"). That belongs in Design-030's resolver — the resolver picks
  the spatial target and emits `/galaxy-map/{spatialPageId}`. This design
  is about the route itself, not the discovery rule for which pageId to
  use.
- **Sub-region targeting beyond the four named drill levels** (overview /
  region / sector / system + body). Trade routes get a special-case
  highlight, not a fifth level.
- **Replacing the existing breadcrumb/click navigation**. The new route
  programmatically triggers the same JS calls those handlers use; they keep
  working the same way they do today.
- **Persisting the deep-link in URL on user navigation**. Today clicking
  through breadcrumbs doesn't update the URL. We don't change that. The
  route is a *one-way* entry point — once the user starts interacting, the
  URL stays at `/galaxy-map/{pageId}` (stale) and that's fine.

## Approach

### 1. KG node types we drill on

Confirmed via `kg.nodes` probe (2026-04-30, `starwars-dev`):

| Type | Count | Drill behaviour |
| --- | --- | --- |
| `System` | 11,600 | Drill to the cell containing the system → call `selectSystemById` on the JS module |
| `CelestialBody` | 8,628 | Look up the body's system via `kg.edges` (`belongs_to`/`orbits`) → drill to that system → call `selectBodyById` |
| `Sector` | 1,295 | Drill to sector breadcrumb level (existing `OnLevelChanged("sector", name)`) |
| `Region` | 66 | If `name` matches a `GeoRegion` from the geography overview, drill to the region overlay; else fall back to overview |
| `TradeRoute` | 352 | Call new `highlightTradeRouteById` on the JS module — pulses the route, leaves user at overview |
| Other (`Location`, etc.) | n/a | Fall back to overview with toast: *"That entity isn't placed on the galaxy map"* |

The `Location` type (5,810 nodes) is intentionally excluded — it's a generic
catch-all for non-mappable places (caves, rooms, neighbourhoods). They get
the toast.

### 2. API endpoint

A new endpoint on `ApiService`:

```text
GET /api/galaxy-map/locate/{pageId}
→ 200 { type, name, systemId?, region?, sector?, continuity }
→ 404 { reason: "not-spatial" | "not-found" }
```

The endpoint reads `kg.nodes` for type/name and follows one `kg.edges`
hop when the node is a `CelestialBody` to find its system. Single endpoint,
single round-trip. Cheap.

### 3. Route handler in `GalaxyMapUnified.razor`

```razor
@page "/galaxy-map"
@page "/galaxy-map/{PageId:int}"
```

```csharp
[Parameter] public int? PageId { get; set; }

protected override async Task OnParametersSetAsync()
{
    if (PageId is int id && id != _appliedDeepLinkId)
    {
        _appliedDeepLinkId = id;
        await ApplyDeepLink(id);
    }
}

async Task ApplyDeepLink(int pageId)
{
    if (_module is null || _overview is null)
    {
        // Wait for first render. OnAfterRenderAsync replays this when ready.
        _pendingDeepLinkId = pageId;
        return;
    }

    LocateResult? loc = null;
    try { loc = await Http.GetFromJsonAsync<LocateResult>($"api/galaxy-map/locate/{pageId}"); }
    catch { }

    if (loc is null) { Snackbar.Add("Couldn't locate that entity on the map.", Severity.Warning); return; }

    switch (loc.Type)
    {
        case "System":         await _module.InvokeVoidAsync("selectSystemById", loc.SystemId ?? pageId); break;
        case "CelestialBody":  await _module.InvokeVoidAsync("selectBodyById", pageId, loc.SystemId);    break;
        case "Sector":         await _module.InvokeVoidAsync("drillToSector", loc.Name);                 break;
        case "Region":         await _module.InvokeVoidAsync("drillToRegion", loc.Name);                 break;
        case "TradeRoute":     await _module.InvokeVoidAsync("highlightTradeRouteById", pageId);         break;
        default:               Snackbar.Add("That entity isn't placed on the galaxy map.", Severity.Info); break;
    }

    PublishPageContext();
}
```

Re-runs from `OnAfterRenderAsync` if the JS module hadn't initialised yet
when the parameter arrived (fresh navigation case).

### 4. JS module additions

Most of the verbs already exist or have close analogues in
`galaxy-map-unified.js` — the page already drills/selects from breadcrumbs.
We just need stable named entry points:

- `selectSystemById(id)` — already exists internally; expose as a module export.
- `selectBodyById(bodyId, systemId)` — drills to the system, then selects
  the body in the panel. Reuses existing system-drill + body-click handlers.
- `drillToSector(name)` — drives the breadcrumb to the sector level.
  Reuses existing logic that fires when the user clicks a sector chip.
- `drillToRegion(name)` — same shape as the existing region overlay click.
- `highlightTradeRouteById(id)` — new helper. Finds the route in the
  pre-loaded geography data, fires a one-shot pulse animation on its path.
  No drill — leaves user at overview.

If the page is at a deeper level than the deep-link target requires (e.g.
user is at sector level and we deep-link to a region), the JS first calls
`goToOverview()` then drills to the requested level.

### 5. PageContext publication

After deep-linking, call `PublishPageContext()` so the copilot sees the
correct subject right away — same helper that already fires from
`OnLevelChanged` and `OnSystemSelected`. The user can immediately ask
*"tell me about this place"* without first interacting with the map.

### 6. Cross-link integration

This route is the single piece Design-030's `CitationLinks.GalaxyMap`
field points to. Once it lands, every spatial entity citation produced by
either agent gets a *"open on Galaxy Map"* button that hands the user to
the right view.

## Implementation phases

**Phase 1 — Direct types only.**

- New `/api/galaxy-map/locate/{pageId}` endpoint.
- Route handler in `GalaxyMapUnified.razor`.
- JS module exposes the four named verbs (`selectSystemById`,
  `selectBodyById`, `drillToSector`, `drillToRegion`).
- Trade routes deferred — they need the new highlight helper.
- Toast on miss.

**Phase 2 — Trade routes.**

- New `highlightTradeRouteById` JS verb with a brief pulse animation.
- Route handler dispatches `TradeRoute` to it.

**Phase 3 — Pair with Citation Resolver (Design-030).**

- Resolver populates `CitationLinks.GalaxyMap` for direct spatial types in
  Phase 1 of *that* design; for indirect (Battle's location, Character's
  homeworld) in Phase 2 of that design.
- This route handles both transparently — it doesn't care whether the
  pageId came from a direct citation or an indirect resolver hop.

## Open questions

- **CelestialBody → System lookup.** Today's `GeoSystem.CelestialBodies`
  list is keyed by id; a reverse lookup means iterating systems until found.
  The locate endpoint can do this server-side via `kg.edges` (where the
  CelestialBody has a `belongs_to` or `orbits` edge to a System). Falls
  through to a system-cache scan as backup.
- **Region name vs. GeoRegion name mismatch.** Some `kg.nodes` Region
  entries (e.g. "Alaris Expanse") aren't the 12 named overview regions.
  When the name doesn't match, fall back to overview with a toast that
  mentions the region name, so the user knows what they were looking for.
- **What if the entity is filtered out by the user's continuity toggle?**
  The deep-link always lands. The map's filtered view will then either show
  or hide the entity per the user's filter — same as if they had drilled
  manually. We don't second-guess the user's URL. (Future: when Design-029
  lands, the route handler could read continuity and silently swap to
  "Both" for the duration of the page load when the requested entity is
  outside the active filter — but that's surprise navigation; better to
  let the user see their own filter applied.)
- **Should the URL update as the user drills?** No. Design-022 +
  Design-030 already publish PageContext for the copilot; the URL is just
  an entry point. Re-syncing the URL on every breadcrumb click is Phase 4
  if anyone ever needs shareable state mid-session.
- **TradeRoute pulse animation duration.** 2s loop, 3 cycles seems right.
  Tune in implementation.

## Revisit when

- **Indirect deep-linking** becomes a UX need beyond what Design-030's
  resolver provides. (Today the resolver picks the spatial target server-side;
  if we want client-side "show this Battle on the map" without going through
  the resolver, that's a separate concern.)
- **The geography data gains pageIds for sectors/regions** that don't exist
  in `kg.nodes`. Today `galaxy.geography` regions/sectors are name-keyed
  only; if they later carry pageIds, the locate endpoint can short-circuit
  the kg.nodes lookup.
- **The four drill levels are no longer enough** — e.g. an "orbit" or
  "moon" level is added. The route's case-table extends; the JS module
  gains a new verb.
