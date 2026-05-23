# Quickstart: Validate the Node-Detail "Enhance with Holocron" Control

**Feature**: `001-node-detail-holocron-button` · **Date**: 2026-05-23

This is the manual / Chrome DevTools MCP validation walkthrough for the feature (per
constitution Principle IV — UI Changes Validated in a Browser). Run it before reporting
the work complete.

## Prerequisites

- Local AppHost up (`dotnet run --project src/StarWarsData.AppHost` or, for agent
  isolation, `aspire run --isolated --detach`).
- The Frontend dev-environment auth bypass active — confirmed by hitting any
  `[Authorize]`-gated page locally without being redirected to Keycloak.
- Holocron globally enabled. If you need to verify the disabled-state path separately,
  toggle `Settings:HolocronEnabled` to `false` via user-secrets and restart the API.
- Pick a node that has enrichable content — any character/organisation/battle PageId you
  already know works. Anakin (verified in Design-020 testing) is a safe choice.

## Validation steps

1. **Open the node-detail page**: navigate to `http://localhost:<port>/knowledge-graph/nodes/<pageId>`.
2. **Verify the button is present in the header card** (the action-bar with Wookieepedia,
   Graph Explorer, Galaxy Map, Timeline, etc.). The "Enhance with Holocron" control must
   render in the same row, with the same icon and label as the equivalent control on the
   `/knowledge-graph` row-expand surface.
3. **Snapshot the DOM** (`mcp__chrome-devtools__take_snapshot`) and confirm the button's
   markup matches the intent. Take a screenshot for the PR.
4. **Read the browser console** (`mcp__chrome-devtools__list_console_messages`).
   Acceptance: no Blazor circuit drops, no JS interop errors, no MudBlazor warnings
   introduced by this change.
5. **Click the button**. Confirm:
   - The `HolocronProgressDialog` opens.
   - The button enters its "Enhancing… (view)" in-progress visual immediately.
   - The dialog stepper progresses through Queued → Discovering → … → Completed.
6. **Close the dialog after completion**. Confirm:
   - The "Holocron-added attributes" section in the panel re-renders with new entries
     (proof that the page's `RefreshAsync` call triggered a panel reload).
7. **Re-click the button after completion**. Confirm a fresh run launches (no leftover
   "already running" lock).
8. **Open the dialog, then close it mid-run** (background it). Re-open from the same
   button. Confirm the dialog re-attaches to the in-flight workflow (pre-check path)
   rather than POSTing a duplicate kick-off.
9. **Resize to mobile** (414×896 via `mcp__chrome-devtools__resize_page`) and re-snapshot.
   The button must stay reachable inside the header chrome and must not overflow the
   container.
10. **Log out / browse without auth** (or temporarily disable the dev auth bypass).
    Confirm the button is *not* rendered (the `<AuthorizeView Context="auth">` wrapper
    is intact).

## Regression guard

11. Navigate to `/knowledge-graph`, expand a node's detail row, and confirm:
    - The in-panel "Enhance with Holocron" button is still present (it should be — the
      panel's `ShowHeader=true` default is unchanged on this surface).
    - Clicking it still works end-to-end (no shared state was inadvertently broken by
      the new `RefreshAsync` method on the panel).
12. From the row-expand, change the global continuity filter. Confirm the panel content
    refreshes (this is the *latent* staleness gap research.md flagged — if Option A's
    `RefreshAsync` was *also* wired into `OnGlobalFilterChanged`, this will work for
    the first time; if not, behaviour is unchanged from today).

## What "done" looks like

All 12 checks pass with screenshots and console-clean snapshots captured in the PR.
Any failure means the work is not yet complete — see Principle IV: "Type-check passing
and a successful build are necessary but NOT sufficient".
