# Research: Node-Detail "Enhance with Holocron" Control

**Feature**: `001-node-detail-holocron-button` · **Date**: 2026-05-23

The spec has no [NEEDS CLARIFICATION] markers. This document records the one design
question the implementation has to settle and the decision taken, plus the
already-verified facts about the surrounding code that constrain it.

## Decision 1 — How to refresh `NodeDetailPanel` after a dialog launched from the page chrome closes

### Context

The existing in-panel button (NodeDetailPanel.razor:548-595) opens the
`HolocronProgressDialog` and, on close, calls the panel's private `LoadAllAsync()` to
re-fetch labels / edges / node-enrichments so newly written enrichments appear inline.

When the equivalent control lives in the **parent page's** header chrome instead, the
parent has no access to the panel's private reload path. The panel guards its
`OnParametersSetAsync` with `if (_loadedForPageId == Node.Id) return;`, so the typical
"set Node = null and back" trick is insufficient — the guard skips the reload because
the Node identity does not change. (This is also the latent reason `OnGlobalFilterChanged`
in `KnowledgeGraphNodeDetail.razor:245-259` doesn't actually refresh the panel today —
worth a follow-up note, but out of scope here.)

### Options considered

| Option | Decision | Why / Why not |
|--------|----------|---------------|
| **A. Expose `public Task RefreshAsync()` on `NodeDetailPanel`; capture via `@ref` in the page and call after dialog close.** | ✅ Chosen | Lowest blast radius — one method addition + one `@ref` wire-up. Mirrors the existing `LoadAllAsync()` flow exactly. Side-benefit: fixes the latent global-filter staleness in passing if we route the existing `OnGlobalFilterChanged` through the same method. |
| B. Add a `RefreshTrigger` int parameter that the panel watches in `OnParametersSetAsync`; bump it from the page to force reload. | ❌ | More indirect than A, adds a "magic counter" that obscures intent, doesn't solve the guard cleanly (still need to track-and-bump in the panel). |
| C. Move the button entirely into `NodeDetailPanel` via a new `ShowHolocronAction` parameter that is independent of `ShowHeader`, rendering it as a small bar above the rest of the panel content. | ❌ | The page's header card already owns the action-bar layout (Wookieepedia, Graph Explorer, Galaxy Map, Timeline, Character timeline, Holocron history). Moving the Holocron action into the panel for this one surface fragments where the actions live and breaks the visual grouping with the other page-level chrome buttons. Also strictly larger change than A. |
| D. Set `ShowHeader="true"` on the page; let the panel render its full header. | ❌ | Would duplicate every other action button (Wookieepedia / Graph Explorer / etc.) that the page already shows in its own chrome. Visual mess; not an option. |

### Decision: Option A

`NodeDetailPanel` exposes a public `Task RefreshAsync()` that:

1. Resets `_loadedForPageId = null`
2. Awaits `LoadAllAsync()`

`KnowledgeGraphNodeDetail.razor` captures the panel via `<NodeDetailPanel @ref="_panel" … />`
and, after `HolocronProgressDialog` closes with a terminal stage, calls
`await _panel.RefreshAsync()` from the same handler that opens the dialog.

### Optional adjacent fix (worth raising, not required for this story)

While we're touching `NodeDetailPanel`, the page's `OnGlobalFilterChanged` could call
`RefreshAsync` for free, closing the latent staleness gap. **Not in scope** for this
spec — flag in tasks.md as a follow-up only.

## Verified facts about the surrounding code (no decision needed)

These are findings from reading the existing implementation, captured so the plan / tasks
don't re-investigate them:

| Concern | Verified location | Note |
|---------|-------------------|------|
| The button + dialog already exist | `NodeDetailPanel.razor:60-91, 548-595` | Visible only when `ShowHeader=true`. |
| The standalone page hides the panel header | `KnowledgeGraphNodeDetail.razor:113` | `ShowHeader="false" ShowImageInline="false"`. |
| The dialog handles `HolocronEnabled=false` | `HolocronProgressDialog.razor:185-191` | Returns `503`; dialog auto-closes with a snackbar warning. The page-chrome button does NOT need to repeat this check; just open the dialog. |
| The dialog handles "already running" | `HolocronProgressDialog.razor:170-191` | Pre-checks status; if non-terminal exists, re-attaches via polling instead of re-POSTing. Page chrome inherits this behaviour for free. |
| Auth gating | `NodeDetailPanel.razor:64` | `<AuthorizeView Context="auth">` — the page-chrome button MUST use the same wrapping. Dev-only synthetic admin principal is set up in `Frontend/Program.cs` (per `feedback_dev_auth_bypass`) so local validation works without Keycloak. |
| Page already injects `IDialogService` / `ISnackbar`? | `KnowledgeGraphNodeDetail.razor:1-9` | Has `ISnackbar` but **not** `IDialogService`. Adding `@inject IDialogService DialogService` is a one-line addition. |
| Existing per-node Holocron API endpoints | Used at `NodeDetailPanel.razor:548-595` and `HolocronProgressDialog.razor:170-200` | `POST /api/holocron/jobs/enhance/{pageId}` and `GET /api/holocron/jobs/{pageId}/status`. No new endpoints needed. |

## Open questions

None. Implementation can proceed.
