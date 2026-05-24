---
description: "Task list for the node-detail Enhance with Holocron control"
---

# Tasks: Node-Detail "Enhance with Holocron" Control

**Input**: Design documents from `specs/001-node-detail-holocron-button/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [quickstart.md](./quickstart.md)

**Tests**: Tests are NOT included as separate tasks — the feature is wiring of already-verified components (existing per-node button + HolocronProgressDialog + Holocron API endpoints), and constitution Principle IV mandates browser validation as the acceptance gate. Existing Unit-tier tests in `src/StarWarsData.Tests` MUST continue to pass (guarded by T009).

**Organization**: One user story (US1, P1) — this is a single-button parity fix; multiple stories would be artificial.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Maps task to spec.md user story (US1)

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

*No setup tasks required.* The Blazor app already exists; the feature branch
`001-node-detail-holocron-button` and the spec directory were created by
`/speckit-git-feature` and `/speckit-specify`. There is nothing else to scaffold.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

*No foundational tasks required.* The Holocron infrastructure this feature relies on
already ships:

- The `HolocronProgressDialog` component ([src/StarWarsData.Frontend/Components/Shared/HolocronProgressDialog.razor](../../src/StarWarsData.Frontend/Components/Shared/HolocronProgressDialog.razor)) — owns the dialog UX, status polling, and 503/409 handling.
- The Holocron async pipeline API endpoints `POST /api/holocron/jobs/enhance/{pageId}` and `GET /api/holocron/jobs/{pageId}/status` (Design-020).
- The Frontend dev-environment auth bypass (per `feedback_dev_auth_bypass`).

**Checkpoint**: Foundation already ready — User Story 1 can begin immediately.

---

## Phase 3: User Story 1 - Trigger Holocron enrichment from the node I'm looking at (Priority: P1) 🎯 MVP

**Goal**: Render an "Enhance with Holocron" control inside the `/knowledge-graph/nodes/{id}` page's header card with the same UX as the in-panel control on `/knowledge-graph`'s row-expand surface, and ensure the panel refreshes after the dialog closes.

**Independent Test**: Per [quickstart.md](./quickstart.md) steps 1–10 — admin loads the page, sees the button, clicks it, observes the existing HolocronProgressDialog drive Queued → Completed, and on close sees the "Holocron-added attributes" section update inline without leaving the page.

### Implementation for User Story 1

- [X] T001 [US1] Add a public `Task RefreshAsync()` method to `NodeDetailPanel` that resets `_loadedForPageId = null` and awaits `LoadAllAsync()`, in [src/StarWarsData.Frontend/Components/Shared/NodeDetailPanel.razor](../../src/StarWarsData.Frontend/Components/Shared/NodeDetailPanel.razor) (per research.md Decision 1, Option A)

- [X] T002 [US1] Inject `IDialogService DialogService` and add a `NodeDetailPanel _panel = default!;` field with a corresponding `@ref="_panel"` on the `<NodeDetailPanel … />` element, in [src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor)

- [X] T003 [US1] Inside the page's header-card action-bar (the `MudStack Row="true"` block that already hosts Wookieepedia, Graph Explorer, Galaxy Map, Timeline, Character timeline, Holocron history), add the missing "Enhance with Holocron" button — wrapped in `<AuthorizeView Context="auth">`, with the same icon (`Icons.Material.Filled.AutoFixHigh`), label, MudTooltip text, in-progress visual (`MudProgressCircular` + "Enhancing… (view)"), and `_holocronRunning` state field as [NodeDetailPanel.razor:60-91, 451](../../src/StarWarsData.Frontend/Components/Shared/NodeDetailPanel.razor#L60-L91). File: [src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor)

- [X] T004 [US1] Add a private `async Task EnhanceWithHolocron()` method to the page that mirrors [NodeDetailPanel.razor:548-595](../../src/StarWarsData.Frontend/Components/Shared/NodeDetailPanel.razor#L548-L595): open `HolocronProgressDialog` with `PageId = _node.Id` and `NodeName = _node.Name`, await the dialog result, and on terminal stage (`Completed` or `Failed`) call `await _panel.RefreshAsync()` (T001) instead of the panel's own private reload. Wire the button's `OnClick` (T003) to this method. File: [src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraphNodeDetail.razor)

- [X] T005 [US1] Build the solution and confirm zero compile errors: `dotnet build src/StarWarsData.slnx`

**Checkpoint**: User Story 1 is functionally complete. Validation in Phase 4 confirms correctness.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Validation gates and regression guards required before reporting the work complete

- [ ] T006 [P] ⚠ **DEFERRED — needs running AppHost** · Chrome DevTools MCP validation of the new button (constitution Principle IV — NON-NEGOTIABLE): walk through [quickstart.md](./quickstart.md) steps 1–10. Capture: a DOM snapshot of the header card showing the button, a screenshot of the dialog mid-run, a screenshot of the panel's "Holocron-added attributes" section after refresh, and `list_console_messages` output confirming no Blazor circuit drops / JS interop errors / new MudBlazor warnings. Resize to 414×896 and re-snapshot per step 9. Confirm step 10 (button hidden when unauthenticated). **Agent context could not satisfy this**: `aspire ps` reports no AppHost running; booting via `aspire run --isolated --detach` would create an isolated user-secrets store with empty `mongo-*` and `openai-key` parameters, leaving the API unable to back the panel's `api/RelationshipGraph/*` and `api/holocron/jobs/*` calls. Validation MUST be performed manually against a developer-run AppHost before this PR ships — instructions are in [quickstart.md](./quickstart.md).

- [ ] T007 [P] ⚠ **DEFERRED — needs running AppHost** · Regression check on `/knowledge-graph` row-expand surface per [quickstart.md](./quickstart.md) steps 11–12: the in-panel "Enhance with Holocron" button still renders (panel's default `ShowHeader=true` unchanged), still triggers the dialog, and still refreshes after close. Confirms T001's `RefreshAsync` addition did not break the existing in-panel call path. Same reason as T006 — deferred to the developer's local AppHost.

- [X] T008 [P] Run the Unit-tier test suite to guard against regressions: `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"`. All tests MUST pass (constitution Principle III). · **Result**: 197/197 passed in 572ms.

- [ ] T009 Optional adjacent fix flagged by research.md: consider routing the page's existing `OnGlobalFilterChanged` handler through `_panel.RefreshAsync()` to close the latent panel-staleness gap. If included in this PR, validate per [quickstart.md](./quickstart.md) step 12. If deferred, raise as a separate follow-up issue and note in the PR description. · **Status**: Deferred from this PR — keep the change narrowly scoped to FR-001…FR-008. Raise as a follow-up.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: empty, no work
- **Foundational (Phase 2)**: empty, no work
- **User Story 1 (Phase 3)**: can start immediately
- **Polish (Phase 4)**: depends on Phase 3 completion (T005 build clean)

### Within User Story 1

- T001 → enables T004 (T004 calls `_panel.RefreshAsync()`)
- T002 → enables T003 and T004 (need `IDialogService` injected and `_panel` field defined)
- T003 → wires the button visually
- T004 → wires the button's behaviour
- T005 → must follow T001–T004 (build verifies them)

Recommended order: **T002 → T001 → T003 → T004 → T005**. T002 is first because it's the structural setup (injection + ref) the others depend on.

### Within Polish

- T006, T007, T008 are independent and can run in parallel ([P]).
- T009 is optional — defer if you'd rather keep the PR narrowly scoped to spec FR-001…FR-008.

## Parallel Opportunities

- Phase 3 tasks (T001–T005) are all in two files that need coordinated edits; treat them as sequential within one author. Two authors could split T001 (panel) from T002–T004 (page) if desired, but the file count doesn't justify it.
- Phase 4 polish tasks T006, T007, T008 can run in parallel — they touch different surfaces (browser, browser, test runner).

## Implementation Strategy

### MVP scope

User Story 1 IS the MVP. There are no later stories. Ship T001–T008 as the PR; T009 is a one-line judgment call.

### Estimated effort

- Phase 3 (T001–T005): ~30 minutes for an author who knows the codebase
- Phase 4 (T006–T008): ~15 minutes including screenshot capture

### Notes

- The `_holocronRunning` field in T003 lives on the **page**, not the panel — the panel's own `_holocronRunning` flag is fine to leave; they govern different button instances on different surfaces.
- Do **not** wrap the page-chrome button in extra `try/finally` ceremony beyond what the existing in-panel `EnhanceWithHolocron` does — match the canonical pattern exactly (Principle I — no gold-plating).
- Do **not** add new unit tests for this wiring (Principle III — Unit tier is for pure logic; this is integration of existing components and is verified by T006).
