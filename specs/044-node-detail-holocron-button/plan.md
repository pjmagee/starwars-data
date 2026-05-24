# Implementation Plan: Node-Detail "Enhance with Holocron" Control

**Branch**: `001-node-detail-holocron-button` | **Date**: 2026-05-23 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/001-node-detail-holocron-button/spec.md`

## Summary

The "Enhance with Holocron" button is already implemented inside the shared
[NodeDetailPanel.razor](../../src/StarWarsData.Frontend/Components/Shared/NodeDetailPanel.razor)
component's header row (lines 60-91). On the standalone `/knowledge-graph/nodes/{id}` page,
[KnowledgeGraphNodeDetail.razor:113](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor#L113)
mounts the panel with `ShowHeader="false"` because the page renders its own header card
above the panel — and when that header card was built (lines 60-103), every action button
was reproduced *except* the Holocron one. This plan closes that single-button gap.

**Approach**: replicate the existing button + dialog interaction inside the page's
header card. The page already owns a `LoadAsync()` that re-fetches the node DTO; after
the dialog closes, the page calls into the panel to force a fresh data pass so newly
written enrichments appear inline — exactly as the inline button does today.

## Technical Context

**Language/Version**: C# (preview language features), .NET 10 (per `global.json`)

**Primary Dependencies**: Blazor Interactive Server, MudBlazor (MudButton, MudDialog,
MudTooltip, MudProgressCircular), `IDialogService`, `ISnackbar`, `IHttpClientFactory`

**Storage**: N/A — this feature does not touch persistence directly. The Holocron
async pipeline (Design-020) already owns its persistence in
`genai.holocron_checkpoints`, `kg.enrichments`, `kg.edge_enrichments`, `kg.events`.

**Testing**: Chrome DevTools MCP (Principle IV) for browser-level validation; no new
unit/integration tests are strictly required because this change is wiring of existing
verified components. Existing Unit-tier tests in `src/StarWarsData.Tests` MUST continue
to pass.

**Target Platform**: Web — Blazor Interactive Server (StarWarsData.Frontend)

**Project Type**: Existing Web app (a `.razor` page edit + a small `NodeDetailPanel`
API addition, no new projects, no new dependencies)

**Performance Goals**: No regression to node-detail page load (currently ~node fetch +
3 parallel API calls inside the panel). The added button is render-time only.

**Constraints**:

- MUST preserve the existing per-node-control UX exactly (icon, label, tooltip,
  in-progress visual, post-completion refresh).
- MUST respect the same `<AuthorizeView Context="auth">` gating as the existing button.
- MUST NOT trigger duplicate workflows when both the page-chrome button and the panel's
  existing button are active (the panel's button is hidden on this page via
  `ShowHeader="false"`, so this is structurally impossible — but verify in validation).
- MUST gracefully handle the `HolocronEnabled=false` case (the existing dialog already
  surfaces a `503 Service Unavailable` + snackbar warning + closes itself; reuse).

**Scale/Scope**: One Razor page edit. One small public method or callback addition to
`NodeDetailPanel.razor`. No backend changes. No new shared components.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

Evaluated against `.specify/memory/constitution.md` v1.1.0 (7 principles):

| # | Principle | Status | Notes |
| - | - | - | - |
| I | Library-First, Deviations Documented | ✅ | Uses existing MudButton / MudDialog / MudTooltip / AuthorizeView. No deviation. |
| II | Production Data Safety (NON-NEGOTIABLE) | ✅ N/A | Pure UI wiring. The Holocron pipeline behind it already targets the configured DB; this feature adds no DB code. |
| III | Test Tiering & Pre-Commit Gate | ✅ | No tests added; existing Unit-tier suite must still pass. |
| IV | UI Changes Validated in a Browser (NON-NEGOTIABLE) | ⚠ REQUIRED | Chrome DevTools MCP validation is mandatory for this PR. Captured as a task in tasks.md. |
| V | Engineering Docs Stay in Sync | ✅ | No new architectural decision; spec under `specs/001-…/` is the per-feature artifact. Design-020 (the original Holocron async pipeline) is unchanged. No ADR needed. |
| VI | KG-First Data Access at Runtime | ✅ N/A | UI-only; runtime data access unchanged. |
| VII | Global Filter Respect | ✅ | The page already subscribes to `GlobalFilterService.OnChange`; the Holocron button itself is not continuity/realm-scoped (node identity is invariant under the filter). |

**Result**: All gates pass. No `Complexity Tracking` entries required.

## Project Structure

### Documentation (this feature)

```text
specs/001-node-detail-holocron-button/
├── plan.md                        # This file
├── spec.md                        # WHAT/WHY (already written)
├── research.md                    # Phase 0 — decision: how to refresh the panel
├── quickstart.md                  # Phase 1 — validation walkthrough
├── tasks.md                       # Phase 2 (not produced by this command)
└── checklists/
    └── requirements.md            # Spec quality checklist (already written)
```

`data-model.md` and `contracts/` are deliberately omitted — no new entities, no new
external interfaces. The feature reuses `POST /api/holocron/jobs/enhance/{pageId}` and
`GET /api/holocron/jobs/{pageId}/status` which already exist and have no shape change.

### Source Code (repository root)

This is an edit to an existing Blazor app — no new projects or directory restructure.
Only these files are touched:

```text
src/StarWarsData.Frontend/
├── Components/
│   ├── Pages/
│   │   └── KnowledgeGraphNodeDetail.razor   # ← add the missing button + dialog launch + panel-refresh hook
│   └── Shared/
│       ├── NodeDetailPanel.razor            # ← expose a public RefreshAsync() (or equivalent) so the page can
│       │                                       force a panel reload after the dialog closes
│       └── HolocronProgressDialog.razor     # (unchanged — already the canonical dialog component)
```

**Structure Decision**: Edit in place. No restructuring, no new shared component
extraction, no abstraction layer. The single-button parity gap does not justify it
(Principle I — don't reach beyond the standard MudBlazor API or pre-emptively
abstract).

## Complexity Tracking

> Filled only if Constitution Check has violations that must be justified.

No constitution violations identified. This section intentionally left empty.
