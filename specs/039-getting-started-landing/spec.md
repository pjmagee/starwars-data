# Design-039: Getting Started landing page

**Status:** Shipped 2026-05-19.
**Author:** Patrick Magee + Claude
**Related:** [Design-037 Misc site-activity dashboard](../037-misc-site-activity-dashboard/spec.md), [ADR-009 public read-only corpus stats](../../eng/adr/009-public-readonly-corpus-stats-surface.md), [Design-022 Page-Aware Copilot/SP-4](../022-galaxy-map-copilot/spec.md)

## Problem

`/` rendered the full `/ask` chat UI immediately. A first-time visitor lands
in a powerful but unexplained chat box with no idea what the site *is* or what
else it can do (galaxy map, timelines, graph explorer, Holocron AI, SP-4).

## Approach

A friendly, ELI5 **Getting Started** page that is the landing for new users;
returning users go straight to chat.

- **`GettingStarted.razor`** — `@page "/"` + `@page "/getting-started"`.
  Hero welcome + feature cards (Chat & Search flagged "Start here", then
  Galaxy Map, Timeline, Character Timelines, Data Tables, Graph Explorer,
  Holocron AI), an SP-4 companion callout, and pointers to
  about/costs/privacy/terms for the fine print. Live KG/article counts come
  from the existing public `GET /api/stats/corpus` (Design-037); the page
  degrades to generic copy if that fetch fails. Unlike `/ask`, SP-4 is
  **not** suppressed here: the companion callout has a **Show/Hide SP-4**
  button that opens/closes the real right-hand panel so a new user can see
  exactly what it is (updated 2026-05-19).
- **SP-4 open state moved to `LayoutService`** (`CopilotOpen` +
  `SetCopilotOpen`/`ToggleCopilot`, default open) as the single source of
  truth, so the Getting Started button and the app-bar toggle drive the same
  drawer. `MainLayout` binds the drawer to `Layout.CopilotOpen` and already
  re-renders on `Layout.OnChange`; the page re-renders its button label the
  same way. No `@Body` re-parenting (the drawer is still a sibling — Design-022).
- **`Ask.razor`** lost its `@page "/"` (kept `/ask`, `/ask/{SessionId:guid}`).
- **First-visit redirect** — a functional-only `localStorage` flag
  `sw-welcomed`. An inline `<head>` IIFE in `App.razor`
  (`redirectWelcomedRoot`) runs *before Blazor boots*: if the path is exactly
  `/` and the flag is set, `location.replace('/ask')` — so returning users
  never flash the welcome page (same zero-flash pattern as the existing
  synchronous text-size apply). `GettingStarted` sets the flag in
  `OnAfterRenderAsync` (first render) via `swSetWelcomed()`. Not tracking, so
  it needs no cookie consent.
- **Nav** — a permanent top "Getting Started" link so anyone can revisit.

## Why a `<head>` redirect, not a Blazor one

Prerender is disabled (`InteractiveServerNoPrerender`), so a Blazor-side
redirect would render the welcome page first and then bounce — a visible
flash on every return visit. The synchronous head script navigates away
during HTML parse, before the circuit connects. Only fires when
`location.pathname === '/'`; `/getting-started` and `/ask` are untouched.

## Validation

Cold-load (Chrome DevTools, no cache):
- First visit `/` → Getting Started renders, live counts populate, zero
  console errors.
- After visiting, `/` → instant `/ask` (no welcome flash).
- `/getting-started` always renders the page regardless of the flag.
- Nav link works; SP-4 stays suppressed on this page.

## Revisit when

If a future requirement needs the welcome state server-side (e.g. A/B or
per-account onboarding), promote `sw-welcomed` from `localStorage` to a real
cookie/user-pref and move the decision server-side; the current scope is
deliberately client-only and zero-infrastructure.
