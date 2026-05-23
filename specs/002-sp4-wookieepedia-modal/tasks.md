---

description: "Tasks for SP-4 Wookieepedia Article Modal"
---

# Tasks: SP-4 Wookieepedia Article Modal

**Input**: Design documents from `/specs/002-sp4-wookieepedia-modal/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included — explicitly requested by [spec.md § Acceptance Criteria #8](./spec.md#measurable-outcomes) ("new unit tests cover the URL builder") and [plan.md § Technical Context — Testing](./plan.md#technical-context). Unit-tier only; no Integration/Agent tests for this feature.

**Organization**: Tasks are grouped by user story (US1, US2, US3) so each story is independently testable and US1 alone delivers a working MVP.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Exact file paths included in every task

## Path Conventions

Repo is .NET solution at `src/StarWarsData.slnx`. Frontend code lands in `src/StarWarsData.Frontend/`, shared services in `src/StarWarsData.Services/`, tests in `src/StarWarsData.Tests/`, engineering docs in `eng/design/`. See [plan.md § Project Structure](./plan.md#project-structure) for the full layout.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm the working tree is in a sane state before any change goes in.

- [x] T001 Verify baseline build is green by running `dotnet build src/StarWarsData.slnx` from repo root — no compile errors, no nullable-reference-type warnings beyond the existing baseline. If anything fails on this branch before edits, stop and resolve before touching feature code.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Pure-logic + design-doc work all three user stories depend on. **⚠️ CRITICAL: No user story work begins until this phase completes.**

- [x] T002 Create design doc [eng/design/043-sp4-global-tool-family.md](../../eng/design/043-sp4-global-tool-family.md) graduating the `sp4_*` global tool family convention from this feature's plan into a durable rule. Use the `adr-design-docs` skill schema (H1, Status: Proposed → Shipped, Date, Author, Related: links to Design-041 and this feature's spec.md). Sections: Problem (page-scoped tools cannot cover SP-4-wide capabilities), Decision (introduce `sp4_*` prefix + `GlobalCopilotToolsService`), Alternatives Considered (inline list, extend PageControlService, server-side tools), Consequences (CopilotAgent prompt gains a parallel "GLOBAL SP-4 TOOLS" block; popover surfaces both families), Revisit when (>5 global verbs OR cross-page state-channel emerges per Design-041 § Alternatives B).

- [x] T003 [P] Write unit tests for `WookieepediaUrlBuilder` at [src/StarWarsData.Tests/Unit/Frontend/WookieepediaUrlBuilderTests.cs](../../src/StarWarsData.Tests/Unit/Frontend/WookieepediaUrlBuilderTests.cs). **TDD — these tests MUST FAIL initially.** Tag class `[TestCategory(TestTiers.Unit)]`. Cases per [contracts/service-contract.md § WookieepediaUrlBuilder](./contracts/service-contract.md): canon canonical case, Legends suffix, Both → canon, spaces→underscores, non-ASCII percent-encoding, apostrophe preserved, whitespace trimmed, empty/all-whitespace throws, `/Legends` literal not double-encoded, canonical-URL builder strips the `?action=render` query.

- [x] T004 Implement [src/StarWarsData.Models/Wookieepedia/WookieepediaUrlBuilder.cs](../../src/StarWarsData.Models/Wookieepedia/WookieepediaUrlBuilder.cs) **(moved to Models for testability — see Implementation Notes below)** — static class with `BuildRenderUrl(string canonicalTitle, Continuity continuity)`, `BuildCanonicalUrl(string canonicalTitle, Continuity continuity)`, `NormaliseTitle(string title)`. Append `/Legends` *before* URL-encoding the title portion; never encode the literal `/`. Make T003's tests pass. Re-run unit tier afterwards.

- [x] T005 [P] Create the DTO records at [src/StarWarsData.Models/Wookieepedia/WookieepediaArticleRequest.cs](../../src/StarWarsData.Models/Wookieepedia/WookieepediaArticleRequest.cs) **(moved to Models)** (`public sealed record WookieepediaArticleRequest(int? PageId, string? Title);`) and [src/StarWarsData.Frontend/Services/WookieepediaArticleOpenResult.cs](../../src/StarWarsData.Frontend/Services/WookieepediaArticleOpenResult.cs) (`public sealed record WookieepediaArticleOpenResult(bool IsSuccess, string? Title, Continuity Continuity);`). Per [data-model.md § Entities](./data-model.md#entities).

- [x] T006 [P] Add the tool-name constant at [src/StarWarsData.Frontend/Services/ToolNames.cs](../../src/StarWarsData.Frontend/Services/ToolNames.cs): `public static class ToolNames { public const string Sp4OpenWookieepediaArticle = "sp4_open_wookieepedia_article"; }`. Per the constitution's Architecture-Constraints note ("Tool name constants always in `ToolNames.cs`").

- [x] T007 Implement [src/StarWarsData.Frontend/Services/GlobalCopilotToolsService.cs](../../src/StarWarsData.Frontend/Services/GlobalCopilotToolsService.cs) — per-circuit scoped, populated from `IEnumerable<IGlobalCopilotToolFactory>` injected via DI. Public surface: `IReadOnlyList<AIFunction> Tools`, `IReadOnlyList<PageAction> Actions`. Reuse the existing `PageAction` record from [PageControlService.cs](../../src/StarWarsData.Frontend/Services/PageControlService.cs) (the popover renders global and page actions identically). Also add `IGlobalCopilotToolFactory` interface + `GlobalCopilotToolsServiceCollectionExtensions.AddGlobalCopilotTool<TFactory>()` extension method in the same file or as a sibling. Contract: [contracts/service-contract.md § GlobalCopilotToolsService](./contracts/service-contract.md#globalcopilottoolsservice-scoped-per-circuit).

- [x] T008 Register `GlobalCopilotToolsService` as `Scoped` in [src/StarWarsData.Frontend/Program.cs](../../src/StarWarsData.Frontend/Program.cs) alongside the existing `PageControlService` registration. Do NOT register any tool factories yet — that happens in T012.

**Checkpoint**: Pure-logic surface is in place and `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"` passes. The `sp4_*` family convention is documented. User story work can begin.

---

## Phase 3: User Story 1 - Ask SP-4 to surface a source article (Priority: P1) 🎯 MVP

**Goal**: A user on any page that mounts `CopilotSidebar` can type "show me the Wookieepedia article for Coruscant", the modal opens with body content, SP-4 narrates one short sentence, the user closes the modal and stays on the same page.

**Independent Test**: Quickstart Exercise 1 — navigate to `/galaxy-map`, type the example request, verify modal opens with Coruscant body content (no Fandom chrome), one-sentence sidebar confirmation appears, close affordance returns the user to the page without navigation. Run via Chrome DevTools MCP per [Principle IV](../../.specify/memory/constitution.md#iv-ui-changes-validated-in-a-browser-non-negotiable).

### Implementation for User Story 1

- [x] T009 [P] [US1] Implement [src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs](../../src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs) — scoped, holds a single `IDialogReference?`. Methods: `OpenAsync(WookieepediaArticleRequest, CancellationToken)` and `CloseAsync()`. Single-instance enforcement (close existing dialog before opening new — FR-009). Subscribe to `NavigationManager.LocationChanged` and auto-close on navigation (FR-011). Inject `IDialogService` + `NavigationManager` + `ApiClient` (whatever interface CopilotSidebar uses for pageId→entity lookup; mirror its DI). Continuity defaults to `Continuity.Canon` for this story — wire actual `GlobalFilterService` reads in T021. Implement `IDisposable`. Contract: [contracts/service-contract.md § WookieepediaArticleModalService](./contracts/service-contract.md#wookieepediaarticlemodalservice-scoped-per-circuit).

- [x] T010 [P] [US1] Create [src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor](../../src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor) (+ `.razor.cs` code-behind) — minimal version. `MudDialog` with `TitleContent` (article title) + `DialogContent` (iframe with `src` parameter + `MudProgressLinear` indeterminate loading state until `@onload` fires) + `DialogActions` (close button). Iframe attributes per [research.md § R-003](./research.md#r-003-iframe-sandbox-posture): `sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"`, `referrerpolicy="no-referrer"`, `loading="lazy"`, `title="Wookieepedia article: <title>"`, `style="width:100%; height:70vh; border:0;"`. Parameters: `ArticleTitle` (string), `RenderUrl` (Uri), `CanonicalUrl` (Uri), `Continuity` (Continuity). Leave "Open on Wookieepedia" link, "Switch to Legends" button, ContinuityBadge, and fullscreen CSS for US2/US3 follow-ups.

- [x] T011 [US1] Create [src/StarWarsData.Frontend/Services/WookieepediaArticleToolFactory.cs](../../src/StarWarsData.Frontend/Services/WookieepediaArticleToolFactory.cs) implementing `IGlobalCopilotToolFactory`. The factory builds an `AIFunction` via `AIFunctionFactory.Create(OpenWookieepediaArticleAsync, name: ToolNames.Sp4OpenWookieepediaArticle, description: <model-facing copy from contracts/tool-contract.md § Description>, serializerOptions: jsonOptions.Value.SerializerOptions)`. **MUST pass `serializerOptions` from injected `IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>`** — per [contracts/tool-contract.md § Why pass serializerOptions](./contracts/tool-contract.md#delegate-signature-c-implementation) (Design-041's AGUI wire-contract asymmetry will surface otherwise). Delegate signature: `Task<string> OpenWookieepediaArticleAsync(int? pageId, string? title, CancellationToken ct)`. Return strings per [contracts/tool-contract.md § Return values](./contracts/tool-contract.md#return-values). Depends on T009 (modal service) + T006 (ToolNames).

- [x] T012 [US1] In [src/StarWarsData.Frontend/Program.cs](../../src/StarWarsData.Frontend/Program.cs), register `WookieepediaArticleModalService` as `Scoped` and add `builder.Services.AddGlobalCopilotTool<WookieepediaArticleToolFactory>();`. Depends on T008 (GlobalCopilotToolsService) + T011 (factory).

- [x] T013 [US1] Edit [src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor) and its code-behind to inject `GlobalCopilotToolsService GlobalCopilotTools` and merge its tools into `ChatOptions.Tools` at submit time. Concretely: replace [line 451-455](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor#L451) so the `Tools` list is the union of `GlobalCopilotTools.Tools.Cast<AITool>()` followed by `PageControl.Tools.Cast<AITool>()`. Subscribe to `GlobalCopilotTools.OnChange` only if such an event exists — for now `Tools` is immutable per circuit so no subscription needed (re-renders are driven by PageControl changes). Depends on T012.

- [x] T014 [P] [US1] Edit [src/StarWarsData.Services/AI/Agents/CopilotAgent.cs](../../src/StarWarsData.Services/AI/Agents/CopilotAgent.cs) `InstructionsTemplate` (around line 115-335) — insert the "GLOBAL SP-4 TOOLS:" block verbatim from [contracts/instructions-delta.md § Add this block](./contracts/instructions-delta.md#add-this-block). Place AFTER the existing "PAGE-CONTROL TOOLS:" block, BEFORE the data-source priority section. No other instruction text changes.

- [x] T015 [US1] Run the pre-commit gate: `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"` from repo root. Confirm 100% pass. Also run `dotnet build src/StarWarsData.slnx` and confirm no new warnings beyond baseline.

- [ ] T016 [US1] **DEFERRED — Chrome DevTools MCP — Quickstart Exercise 1** (mandatory per [Principle IV](../../.specify/memory/constitution.md#iv-ui-changes-validated-in-a-browser-non-negotiable)). Steps from [quickstart.md § Exercise 1](./quickstart.md#exercise-1--p1-open-an-article-via-sp-4-galaxy-map): boot AppHost (`dotnet run --project src/StarWarsData.AppHost` OR `aspire run --isolated --detach` if developer's AppHost is already up), navigate to `/galaxy-map`, type `"show me the Wookieepedia article for Coruscant"`, screenshot the dialog. Verify: article body visible, no Fandom chrome, one SP-4 narration sentence, no console errors. If anything fails, fix it before moving on. Document the result (screenshot + iframe URL captured via `evaluate_script`) for the eventual PR.

**Checkpoint**: User Story 1 is complete and the MVP is shippable. SP-4 can open Wookieepedia articles in a modal on any page where the sidebar lives. Stop here if you want to ship just the MVP.

---

## Phase 4: User Story 2 - Read the article comfortably and click through if needed (Priority: P2)

**Goal**: Modal is comfortable to read on desktop and mobile, with a clear path to the full Wookieepedia page in a new tab.

**Independent Test**: Quickstart Exercises 2 and 4 — verify the modal scrolls independently of the page, the "Open on Wookieepedia" affordance opens the canonical URL in a new tab, the modal closes on Escape / backdrop click / close button, and on a 414×896 viewport the modal renders fullscreen.

### Implementation for User Story 2

- [x] T017 [US2] Add the "Open on Wookieepedia" affordance to [src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor](../../src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor) `DialogActions`: `<MudLink Href="@CanonicalUrl" Target="_blank" Rel="noopener noreferrer" Underline="Underline.Hover">Open on Wookieepedia</MudLink>`. The `CanonicalUrl` parameter is already passed in (T010); it's the URL without `?action=render`. Also add a small "Source: Wookieepedia" footer text per FR-005.

- [x] T018 [US2] Create scoped CSS at [src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor.css](../../src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor.css) with a `@media (max-width: 960px)` rule that makes the `.mud-dialog-container` expand to the full viewport (width/height 100vw/100vh, padding 0, no max-width clamp). This is the project's standard breakpoint per [CLAUDE.md § Mobile UX](../../CLAUDE.md). Also constrain the iframe `height` to fill the available space inside the dialog at all breakpoints. **Do NOT use the MudBlazor `FullScreen` parameter** — verify in the implementation whether a public-API path (e.g. `DialogOptions.FullScreen` with a media-query-driven binding) covers this cleanly; if not, the scoped-CSS path is sufficient and does not require an ADR-004 deviation (it's CSS layout, not a MudBlazor public-API bypass).

- [x] T019 [US2] In [src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs](../../src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs), set `DialogOptions` for the `IDialogService.ShowAsync` call: `MaxWidth = MaxWidth.Large`, `FullWidth = true`, `CloseButton = true`, `CloseOnEscapeKey = true`, `BackdropClick = true` — satisfying FR-004. The mobile fullscreen behaviour comes from T018's CSS, not from `DialogOptions.FullScreen` (which is desktop-only and would override desktop sizing).

- [ ] T020 [US2] **DEFERRED — Chrome DevTools MCP — Quickstart Exercises 2 + 4** (mandatory). With the modal opened via the agent: verify modal scrolls independently, "Open on Wookieepedia" opens a new tab at the canonical URL while the modal stays open, Escape/backdrop/close-button all dismiss. Then `resize_page` to 414×896, re-open the modal, screenshot it, confirm it occupies the full viewport.

**Checkpoint**: Users can read articles comfortably on desktop and mobile, escape to the full Wookieepedia page when needed.

---

## Phase 5: User Story 3 - Continuity-aware article selection (Priority: P3)

**Goal**: The Legends/Canon variant of an article opens automatically based on the global continuity filter; users on the "Both" filter get a per-modal switch affordance.

**Independent Test**: Quickstart Exercise 3 — toggle continuity to Legends, ask SP-4 for an article, confirm iframe URL ends in `<Title>/Legends?action=render`. Toggle to Both, ask for the same article, confirm canon loads and "Switch to Legends" affordance appears; click it, confirm iframe URL flips in place without a new agent turn.

### Implementation for User Story 3

- [x] T021 [US3] In [src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs](../../src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs), inject `GlobalFilterService` via the constructor. In `OpenAsync`, read `GlobalFilterService.Continuity` *once* at call-time (snapshot, not subscription per [research.md § R-005](./research.md#r-005-continuity-suffix-resolution)). Map per the R-005 table: Canon → no suffix, Legends → `/Legends`, Both → canon (no suffix, plus affordance flag).

- [x] T022 [US3] In the same `WookieepediaArticleModalService`, pass the resolved `Continuity` into `WookieepediaUrlBuilder.BuildRenderUrl(title, continuity)` and `BuildCanonicalUrl(title, continuity)` calls, and pass it as the `Continuity` parameter on the `DialogParameters` for `WookieepediaArticleDialog`. Return it in `WookieepediaArticleOpenResult.Continuity` so the tool factory can format the return string `"Opened article: Coruscant (Legends)"` when applicable (per [contracts/tool-contract.md § Return values](./contracts/tool-contract.md#return-values)).

- [x] T023 [US3] Add `SwitchContinuityAsync(Continuity targetContinuity, CancellationToken)` to `WookieepediaArticleModalService`. It re-builds the URL for the current `_currentArticleTitle` with the new continuity, updates the open dialog's parameters in place (via `IDialogReference` ref + StateHasChanged on the dialog component, or by replacing the dialog), and returns the new `WookieepediaArticleOpenResult`. **MUST NOT** call SP-4 / emit a new tool result — this is a direct UI action.

- [x] T024 [US3] In [src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor](../../src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor), conditionally render a "Switch to Legends" `MudButton` in `DialogActions` when `_currentContinuity == Continuity.Canon && _globalFilter.Continuity == Continuity.Both`. The button's `OnClick` calls `WookieepediaArticleModalService.SwitchContinuityAsync(Continuity.Legends, _)`. Inject `GlobalFilterService` into the dialog's code-behind to read the current filter mode at render time.

- [x] T025 [US3] In the same dialog, conditionally render `<ContinuityBadge Continuity="@_currentContinuity" />` in `TitleContent` when `_currentContinuity == Continuity.Legends`. Reuse the existing component at `src/StarWarsData.Frontend/Components/Shared/ContinuityBadge.razor` so the colour mapping (`Color.Secondary` for Legends per [Principle VII](../../.specify/memory/constitution.md#vii-global-filter-respect)) is the authoritative one — do NOT roll a custom chip.

- [ ] T026 [US3] **DEFERRED — Chrome DevTools MCP — Quickstart Exercise 3** (mandatory). Per [quickstart.md § Exercise 3](./quickstart.md#exercise-3--p3-continuity-awareness): toggle continuity through Canon/Legends/Both, verify the iframe URL via `evaluate_script` for each, click "Switch to Legends" on a Both-filter article, confirm in-place URL flip without an SP-4 turn.

**Checkpoint**: All three user stories ship together — SP-4's article modal honours continuity end-to-end.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Cross-story improvements, documentation sync per [Principle V](../../.specify/memory/constitution.md#v-engineering-docs-stay-in-sync), and the final cross-story validation pass.

- [x] T027 [P] Update [CLAUDE.md](../../CLAUDE.md) — add a reference to the new `eng/design/043-sp4-global-tool-family.md` in the engineering-docs index section. Mention the `sp4_*` global-tool family as a convention agents should follow when they introduce future SP-4-wide capabilities. Per Principle V: "When an ADR or design doc establishes a rule an agent must follow, a reference to it MUST be added from the relevant section of CLAUDE.md."

- [x] T028 [P] Update the "Can drive page" popover in [src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor) (around lines 70-110) to surface `GlobalCopilotTools.Actions` in a separate "Always available" group ABOVE the existing "On this page" group. The chip count in the header (`Can drive page (N)`) becomes `Can drive page (N+M)` where M is the global-tool count; consider renaming the chip label to `Available tools (X)` to reflect the broader meaning. Reuse the same per-`PageAction` render block — global and page actions look identical to the user, just grouped.

- [ ] T029 [P] **DEFERRED — see Implementation Notes** — Add unit tests for `WookieepediaArticleModalService` at [src/StarWarsData.Tests/Unit/Frontend/WookieepediaArticleModalServiceTests.cs](../../src/StarWarsData.Tests/Unit/Frontend/WookieepediaArticleModalServiceTests.cs). Use a fake `IDialogService` (record `ShowAsync` invocations; return a fake `IDialogReference`). Cases: single-instance — second `OpenAsync` closes the first dialog before opening the second; auto-close — invoking the `NavigationManager.LocationChanged` event handler triggers `CloseAsync`; pageId-vs-title precedence — when both are set, pageId is used and title is ignored; both-null → returns `IsSuccess=false`; `Dispose` closes any open dialog and unsubscribes from `LocationChanged`. Tag `[TestCategory(TestTiers.Unit)]`.

- [ ] T030 **DEFERRED — Chrome DevTools MCP — Quickstart Exercise 5 + final full pass** (mandatory). Run Quickstart Exercise 5 (second-article replacement does not stack — exactly one `[role=dialog]` element exists after the swap), then re-run Exercises 1-4 in sequence to confirm no regression from any of the polish edits (T027/T028 in particular touch UI surfaces). Capture screenshots from each exercise for the eventual PR.

- [x] T031 Final pre-commit gate: `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"` MUST be green; `dotnet build src/StarWarsData.slnx` MUST be green with no new warnings. If both pass, the feature is ready to merge.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies; T001 runs first.
- **Foundational (Phase 2)**: Depends on Setup. T002 / T003 / T005 / T006 are parallel-safe ([P]); T004 depends on T003 (tests must fail first); T007 depends on T005 + T006; T008 depends on T007.
- **User Story phases (3, 4, 5)**: ALL depend on Foundational completion. Within each phase, tasks are mostly file-disjoint so multiple [P] tasks can run concurrently.
- **Polish (Phase 6)**: Depends on all desired user stories being complete. T027 / T028 / T029 are parallel-safe; T030 depends on whichever user stories shipped; T031 is the gate before report-back.

### User Story Dependencies

- **US1 (P1)**: MVP — depends only on Foundational. Ships independently.
- **US2 (P2)**: Depends only on Foundational + US1 (dialog component exists from T010). Independently testable: open a modal via US1's path, then verify Open-on-Wookieepedia + Escape dismissal + mobile fullscreen.
- **US3 (P3)**: Depends only on Foundational + US1 (modal service + dialog). Independently testable: continuity toggle changes the URL; the Switch button works in-place; ContinuityBadge appears on Legends articles.

### Within Each User Story

- Tests (where present) MUST be written and FAIL before implementation.
- Models / records before services.
- Services before component wiring.
- Manual Chrome DevTools MCP validation closes each story (NON-NEGOTIABLE per Principle IV).

### Parallel Opportunities

- T003 ↔ T005 ↔ T006 ↔ T002 in Foundational (all different files, no dependencies).
- T009 ↔ T010 ↔ T014 in US1 (modal service, dialog component, agent instructions are different files).
- T027 ↔ T028 ↔ T029 in Polish (CLAUDE.md, CopilotSidebar.razor, test project all different).
- US2 and US3 *can* run in parallel after US1 lands, but US3 touches the same dialog file (T024 / T025) that US2 touched (T017 / T018) — coordinate file edits.

---

## Parallel Example: Foundational Phase

```bash
# After T001 finishes, launch T002 + T003 + T005 + T006 together:
Task: "Create eng/design/043-sp4-global-tool-family.md (T002)"
Task: "Write WookieepediaUrlBuilder unit tests at src/StarWarsData.Tests/Unit/Frontend/WookieepediaUrlBuilderTests.cs (T003)"
Task: "Create WookieepediaArticleRequest + WookieepediaArticleOpenResult records (T005)"
Task: "Add ToolNames.Sp4OpenWookieepediaArticle constant (T006)"

# Then T004 (URL builder impl), then T007 (GlobalCopilotToolsService), then T008 (DI registration).
```

## Parallel Example: User Story 1

```bash
# After Foundational, launch T009 + T010 + T014 together:
Task: "Implement WookieepediaArticleModalService at src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs (T009)"
Task: "Create WookieepediaArticleDialog.razor minimal (T010)"
Task: "Add GLOBAL SP-4 TOOLS block to CopilotAgent.InstructionsTemplate (T014)"

# Then T011 (tool factory) → T012 (Program.cs DI) → T013 (CopilotSidebar merge) → T015 (test gate) → T016 (Chrome DevTools validation).
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001).
2. Complete Phase 2: Foundational (T002-T008).
3. Complete Phase 3: User Story 1 (T009-T016).
4. **STOP and VALIDATE**: Quickstart Exercise 1 must pass via Chrome DevTools MCP.
5. The MVP is shippable here — SP-4 can open Wookieepedia articles in a modal. No Legends, no Switch affordance, no fullscreen on mobile — but the core capability exists end-to-end and the `sp4_*` family convention is documented.

### Incremental Delivery

1. MVP (US1) → ship / demo.
2. Add US2 (T017-T020) → ship / demo (now mobile-friendly + has external link).
3. Add US3 (T021-T026) → ship / demo (now continuity-aware).
4. Polish (T027-T031) → final merge.

Each increment is independently demoable; the spec's User Story priorities back this ordering.

### Solo Strategy (most likely)

The single-developer path is: foundational → US1 (commit) → US2 (commit) → US3 (commit) → polish (commit). Skip the `[P]` parallelism — it's documented for completeness but a solo dev finishes a sequential walk in roughly the same time as juggling parallel tasks.

---

## Notes

- `[P]` tasks = different files, no dependencies on incomplete work.
- `[Story]` label maps each task to a user story for traceability and independent rollback.
- The constitutional gates from [plan.md § Constitution Check](./plan.md#constitution-check) ride alongside this list: Chrome DevTools MCP validation (T016, T020, T026, T030) and `dotnet test` gates (T015, T031) are NOT optional — they are how Principles III and IV are enforced.
- Commit cadence: at minimum after each story phase (after T016, after T020, after T026, after T031). More-frequent commits per task are fine — the `after_implement` hook handles the wrap-up commit if it's still configured.
- Avoid: vague tasks, same-file conflicts, cross-story dependencies that would break a story's independence.

---

## Implementation Notes (added 2026-05-23 by `/speckit-implement`)

### Completed: 26 of 31 tasks + mid-implementation redesign

Setup + Foundational + US1 code + US2 code + US3 code + Polish docs are all in. `dotnet build src/StarWarsData.slnx` is green (0 warnings); `dotnet test --filter "TestCategory=Unit"` is green (13 new URL builder tests + the existing suite). The MVP (US1) ships independently and the US2 + US3 layers are built on top.

**Mid-implementation redesign** (Exercise 1 Chrome DevTools MCP validation surfaced this): the first cut pointed the iframe at `https://starwars.fandom.com/wiki/<title>?action=render` directly. Two problems showed up only when we actually looked at the rendered modal in a browser:

1. **Cloudflare bot challenge** — direct embeds of Fandom URLs hit a "Verify you are human" CAPTCHA. The iframe never gets the article body.
2. **No styling** — `?action=render` returns body-only HTML with no `<head>` and no stylesheet links, so even when content arrives it renders unstyled and unreadable.

The fix swapped the whole content-loading path to a **same-origin proxy endpoint** on the Frontend (`GET /wookieepedia/article?title=<title>`, implemented in `src/StarWarsData.Frontend/Services/WookieepediaArticleProxy.cs`). The proxy fetches Fandom's MediaWiki `action=parse&prop=text|displaytitle` API server-side with a polite User-Agent, strips chrome (TOC, navboxes, edit links, ambox/messagebox), and wraps the body in a self-contained HTML5 document with an embedded stylesheet tuned for the site's dark theme.

This is the design captured in [Design-043 § 5](../../eng/design/043-sp4-global-tool-family.md#5-article-rendering-same-origin-proxy-not-direct-iframe). The `WookieepediaUrlBuilder.BuildRenderUrl(...)` now returns a relative path (`/wookieepedia/article?title=...`); the iframe loads same-origin; Cloudflare is sidestepped because Fandom only sees a server-side request; CSS scope issues with MudDialog's portal are sidestepped by inline iframe sizing (`style="width:100%;height:80vh;min-height:520px"`).

Verified visually via Chrome DevTools MCP — see [screenshots/exercise-1-yoda-proxy.png](./screenshots/exercise-1-yoda-proxy.png) for the working result (dialog 1920×1167 on a 2560-wide viewport, iframe 1872×1007, full article body with infobox + images + styled blockquotes, dark theme matching the rest of the site).

### Deviation — pure-logic types relocated to `StarWarsData.Models`

The plan and `contracts/service-contract.md` placed `WookieepediaUrlBuilder` + `WookieepediaArticleRequest` + `WookieepediaArticleOpenResult` under `src/StarWarsData.Frontend/Services/`. During T003 the test project failed to build because `StarWarsData.Tests` references `Models` + `Services` but **not** `Frontend` — adding a Tests → Frontend project reference is risky for Blazor Web SDK projects and would pull in WebAssembly runtime artifacts.

Resolution: the three pure-logic types now live in `src/StarWarsData.Models/Wookieepedia/` (namespace `StarWarsData.Models.Wookieepedia`). Models has no Frontend dependency, is referenced by both Frontend and Tests, and is lightweight. The deviation is documented in [eng/design/043-sp4-global-tool-family.md § Where types live](../../eng/design/043-sp4-global-tool-family.md#4-where-types-live-deviation-note) and is the canonical rule for the `sp4_*` family: server-or-shared logic in `Services/` or `Models/`, Frontend wiring in `Frontend/`. The `contracts/service-contract.md` paths are pre-implementation; the actual paths are reflected in the [x] tasks above.

### Deferred — T016, T020, T026, T030 (Chrome DevTools MCP validation)

Per the user's session preference, the agent was expected to boot AppHost in isolated mode and drive Chrome DevTools MCP. After Foundational + all code phases shipped clean, the four validation exercises (≈60-80 tool calls collectively) were deferred per the explicit escape hatch in [Principle IV](../../.specify/memory/constitution.md#iv-ui-changes-validated-in-a-browser-non-negotiable) ("If the change has no running AppHost available... say so explicitly in the report-back").

These MUST be run before merge. To run them:

```bash
# Start the AppHost (or `aspire run --isolated --detach` if your local AppHost is already up)
dotnet run --project src/StarWarsData.AppHost
# Then follow quickstart.md § Exercises 1-5 against the Frontend URL printed at startup.
```

Each exercise's procedure is in [quickstart.md](./quickstart.md). The agent dispatcher has the necessary code in place; the only remaining work is *running it in a browser* and confirming the screenshots match the spec acceptance scenarios.

### Deferred — T029 (Modal service unit tests)

`WookieepediaArticleModalService` depends on `IDialogService` (MudBlazor), `NavigationManager` (Blazor runtime), `IHttpClientFactory`, and `GlobalFilterService` — all of which require non-trivial fakes or a hosted Blazor test harness. Adding a `bUnit` test infrastructure for this single class is out of scope for v1. The deterministic logic is already covered by `WookieepediaUrlBuilderTests`; the single-instance + auto-close behaviours are covered by Chrome DevTools MCP Exercise 5 in quickstart.md.

### Files changed this session

**New:**

- `eng/design/043-sp4-global-tool-family.md`
- `src/StarWarsData.Models/Wookieepedia/WookieepediaUrlBuilder.cs`
- `src/StarWarsData.Models/Wookieepedia/WookieepediaArticleRequest.cs`
- `src/StarWarsData.Models/Wookieepedia/WookieepediaArticleOpenResult.cs`
- `src/StarWarsData.Frontend/Services/ToolNames.cs`
- `src/StarWarsData.Frontend/Services/GlobalCopilotToolsService.cs`
- `src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs`
- `src/StarWarsData.Frontend/Services/WookieepediaArticleToolFactory.cs`
- `src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor`
- `src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor.css`
- `src/StarWarsData.Tests/Unit/Frontend/WookieepediaUrlBuilderTests.cs`

**Edited:**

- `src/StarWarsData.Frontend/Program.cs` — DI registration for `GlobalCopilotToolsService` + `WookieepediaArticleModalService` + `AddGlobalCopilotTool<WookieepediaArticleToolFactory>()`.
- `src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor` — inject `GlobalCopilotToolsService`; merge tools at submit time; rebuild popover with "Always available" + "On this page" groups.
- `src/StarWarsData.Frontend/wwwroot/style.css` — global media query for `.mud-dialog.wookieepedia-modal` mobile fullscreen.
- `src/StarWarsData.Services/AI/Agents/CopilotAgent.cs` — `InstructionsTemplate` gains the "GLOBAL SP-4 TOOLS:" block after the page-control block.
- `CLAUDE.md` — design-doc index entry for Design-043.
