# Implementation Plan: SP-4 Wookieepedia Article Modal

**Branch**: `002-sp4-wookieepedia-modal` | **Date**: 2026-05-23 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/002-sp4-wookieepedia-modal/spec.md`

## Summary

Give SP-4 a **global** client-side tool — `sp4_open_wookieepedia_article` — that opens a MudBlazor dialog whose body is an iframe pointed at Wookieepedia's body-only article endpoint (`https://starwars.fandom.com/wiki/<title>?action=render`). The modal shows the article content (text, headings, infobox, images, internal links) without Fandom site chrome, and SP-4 narrates one short confirmation sentence after firing.

The tool is **global to SP-4** (available wherever `CopilotSidebar` mounts), not page-scoped via `PageControlService` — that's the headline architectural shift in this feature, and the reason a new `specs/043-sp4-global-tool-family/spec.md` companion doc graduates the `sp4_*` tool-family convention out of this `plan.md` into a durable rule for future global SP-4 verbs.

Technical approach (validated against existing code):

- A new **`GlobalCopilotToolsService`** sibling to [PageControlService](../../src/StarWarsData.Frontend/Services/PageControlService.cs) exposes a static `IReadOnlyList<AIFunction>` of SP-4-wide tools. [CopilotSidebar.razor:451-455](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor#L451-L455) merges `GlobalCopilotTools.Tools` with `PageControl.Tools` before populating `ChatOptions.Tools`. The `UseFunctionInvocation` middleware (already wired at line 355) dispatches both sets identically.
- A new **`WookieepediaArticleDialog.razor`** under `src/StarWarsData.Frontend/Components/Shared/` renders the modal: `<MudDialog>` + `<iframe>` + loading/error states + footer with source attribution and Canon/Legends switch affordance.
- A new **`WookieepediaArticleModalService`** (scoped per-circuit) owns single-modal-instance semantics: `OpenAsync(request)` resolves the request to a URL, opens the dialog via `IDialogService`, replaces any prior modal in place. `CloseAsync()` dismisses.
- The tool delegate (the `AIFunction` body) calls `WookieepediaArticleModalService.OpenAsync(...)` and returns a short string (`"Opened article: Coruscant"` / `"Article not found for 'Bothan'"`) for SP-4 to quote.
- The Canon/Legends suffix is resolved from the existing `GlobalFilterService.Continuity` per [Principle VII](../../.specify/memory/constitution.md#vii-global-filter-respect).
- pageId→title resolution uses the existing entity-lookup API (the same path that powers `[Name](/graph-explorer/{pageId})` link resolution in the sidebar's citation card).
- `CopilotAgent.InstructionsTemplate` ([CopilotAgent.cs:115-335](../../src/StarWarsData.Services/AI/Agents/CopilotAgent.cs#L115)) gains a new "GLOBAL SP-4 TOOLS" section describing the `sp4_*` family alongside the existing `<page>_*` page-tool section.

No API service changes, no MongoDB schema changes, no new ETL phase, no new Hangfire job. Purely additive on the Frontend runtime path.

## Technical Context

**Language/Version**: C# preview features on .NET 10 (per `global.json` at repo root and Principle II's architecture constraints).

**Primary Dependencies**:

- **MudBlazor** — for `MudDialog`, `IDialogService`, `MudProgressLinear` (loading state). Public API only; no expected deviation from [eng/adr/004-mudblazor-deviations.md](../../eng/adr/004-mudblazor-deviations.md).
- **Microsoft.Extensions.AI** (`IChatClient`, `AIFunctionFactory.Create`, `ChatOptions.Tools`) — for building the AIFunction the agent calls.
- **Microsoft.Agents.AI.AGUI** 1.6.1-preview — already adopted by the sidebar; the per-turn tools channel carries the new global tool alongside any registered page tools.
- **Microsoft.AspNetCore.Http.Json** (`IOptions<JsonOptions>`) — `AIFunctionFactory.Create` requires the same serializer options threaded through other agent tools to avoid the AGUI wire-contract asymmetry documented in Design-041 § Resolved questions.

**Storage**: N/A — no persistence. The feature reads pageId→title from `kg.nodes` via the existing entity-lookup API path when the agent passes a pageId; otherwise the title comes directly from the tool argument.

**Testing**:

- **Unit tier** ([Principle III](../../.specify/memory/constitution.md#iii-test-tiering--pre-commit-gate)) — pure-logic tests under `src/StarWarsData.Tests/Unit/`:
  - URL builder: title encoding (spaces, slashes, non-ASCII, apostrophes), `/Legends` suffix logic, canonical vs render URL pairs.
  - Continuity resolution: Canon → no suffix; Legends → `/Legends` suffix; Both → no suffix (canon default).
  - Pure-string sanitation of inbound titles (strip leading/trailing whitespace, reject empty).
- **No Integration tier** needed — the feature has no Testcontainers MongoDB surface.
- **No Agent tier** — the LLM-driven path is validated manually via Chrome DevTools MCP per [Principle IV](../../.specify/memory/constitution.md#iv-ui-changes-validated-in-a-browser-non-negotiable).
- **Manual UI validation** — five Chrome DevTools MCP exercises documented in `quickstart.md` covering the P1 / P2 / P3 user stories from `spec.md`.

**Target Platform**: Blazor Interactive Server (Frontend project). Default browser per [user_browser.md](../../../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/user_browser.md) is Brave; Chrome DevTools MCP also drives Brave.

**Project Type**: Web (Frontend-only addition). The plan touches:

- `src/StarWarsData.Frontend/Components/Shared/` — new `WookieepediaArticleDialog.razor` + `.razor.cs` + scoped `.razor.css`.
- `src/StarWarsData.Frontend/Services/` — new `GlobalCopilotToolsService.cs` + `WookieepediaArticleModalService.cs` + URL/title helpers.
- `src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor[.cs]` — merge GlobalCopilotTools.Tools into ChatOptions.Tools; extend the "Can drive page" popover to surface global tools too.
- `src/StarWarsData.Frontend/Program.cs` — DI registration of the two new services.
- `src/StarWarsData.Services/AI/Agents/CopilotAgent.cs` — instructions update for `sp4_*` global-tool family.
- `src/StarWarsData.Tests/Unit/` — new test class(es) for URL builder + continuity resolution.
- `specs/043-sp4-global-tool-family/spec.md` — new design doc capturing the global-tool-family convention.
- `CLAUDE.md` — reference to the new design doc per Principle V.

**Performance Goals**:

- SP-4 turn → modal open: under 10 seconds wall-clock (SC-001).
- Iframe first contentful paint: bounded by Wookieepedia's render endpoint (typically <2s under normal conditions).
- No new server-side cost: zero added MongoDB reads, zero added OpenAI tokens beyond the one extra tool the agent sees per turn (negligible — a tool declaration adds ~50 prompt tokens).

**Constraints**:

- **No proxy / no caching of article HTML** through our API. The iframe loads directly from `starwars.fandom.com` ([Assumptions § 4 of spec.md](./spec.md#assumptions)). If Fandom rate-limits us in future, revisit with a proxy ADR — not now.
- **No new write path** anywhere — the tool is read-only by construction (Principle II is trivially satisfied).
- **Single-instance modal** — only one article modal open at a time (FR-009). The service enforces this by holding a single `IDialogReference?` slot; opening a new article calls `Close()` on the existing one first.
- **Auto-close on host-navigation** — `NavigationManager.LocationChanged` triggers a close (FR-011) so the modal does not occlude the new page.

**Scale/Scope**:

- One new dialog component (~150 LOC Razor + ~80 LOC C# code-behind).
- Two new scoped services (~50 LOC each).
- One new `AIFunction` factory + the `GlobalCopilotToolsService` wiring (~30 LOC).
- ~10 unit tests for the URL builder + continuity logic.
- One new design doc (`specs/043-sp4-global-tool-family/spec.md`, ~150 lines).
- One new entry in CLAUDE.md's design-doc index.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Gates determined from the seven principles in [.specify/memory/constitution.md](../../.specify/memory/constitution.md) (v1.1.0). Every principle is evaluated below; gates that don't apply are marked **N/A** with one-line justification.

### Pre-Phase-0 gate

- **I. Library-First, Deviations Documented** — ✅ PASS. The feature uses MudBlazor (`MudDialog`, `IDialogService`, `MudProgressLinear`) entirely via public APIs. The iframe is a standard HTML element. No new third-party library introduced. No deviation from [eng/adr/004-mudblazor-deviations.md](../../eng/adr/004-mudblazor-deviations.md) expected; if implementation surfaces one (e.g. `MudDialog` cannot do fullscreen on `<960px` via its public parameters), an ADR entry MUST be added to ADR-004's Catalogue per Principle I before merge.
- **II. Production Data Safety (NON-NEGOTIABLE)** — ✅ PASS. The feature has no write path. pageId→title resolution is read-only against the existing entity-lookup API. iframe content lives outside our backend entirely.
- **III. Test Tiering & Pre-Commit Gate** — ✅ PASS. Unit tier covers all deterministic logic (URL builder + continuity suffix). No Integration/Agent tier added. The new tests MUST NOT touch Docker, MongoDB, or OpenAI.
- **IV. UI Changes Validated in a Browser (NON-NEGOTIABLE)** — ✅ PASS (gated). The feature is fundamentally UI. Chrome DevTools MCP validation is mandatory and the quickstart.md script enumerates the five exercises that MUST run before report-back: P1 modal open + body content visual check + console-log read; P2 close + Open-on-Wookieepedia in new tab; P3 Canon vs Legends suffix; mobile 414×896 fullscreen; second-article replacement (no stacking).
- **V. Engineering Docs Stay in Sync** — ✅ PASS (gated). A new `specs/043-sp4-global-tool-family/spec.md` MUST be authored in the same PR that introduces the `sp4_*` tool-family convention (it graduates a standing decision *out* of this `plan.md` into durable knowledge — exactly the Principle V boundary). CLAUDE.md's design-doc index MUST gain a reference. `eng/adr/` is untouched (no architecture decision new enough to need its own ADR — the global-tool-family decision lives correctly in a design doc).
- **VI. KG-First Data Access at Runtime** — ✅ PASS. pageId→title resolution reads `kg.nodes` via the existing API path. No `raw.*` reads anywhere.
- **VII. Global Filter Respect** — ✅ PASS (gated). `GlobalFilterService.Continuity` MUST drive the `/Legends` suffix per FR-012. The modal subscribes to `GlobalFilterService.OnChange` only to dispose itself if the user toggles continuity mid-read (per edge cases in spec.md — explicit decision to NOT auto-reload). The "Switch to Legends" affordance for the `Both` filter case opens the Legends variant in-modal without re-querying SP-4.

**Result**: ✅ All gates pass. No Complexity Tracking entries required at the pre-Phase-0 gate.

### Post-Phase-1 re-check

(Re-run after Phase 1 artifacts exist; recorded below the Project Structure section.)

## Project Structure

### Documentation (this feature)

```text
specs/002-sp4-wookieepedia-modal/
├── plan.md              # This file
├── spec.md              # User-facing spec (already shipped)
├── research.md          # Phase 0 output (this command)
├── data-model.md        # Phase 1 output (this command)
├── quickstart.md        # Phase 1 output (this command)
├── contracts/
│   ├── tool-contract.md            # AIFunction input/output shape
│   ├── instructions-delta.md       # CopilotAgent prompt addition
│   └── service-contract.md         # GlobalCopilotToolsService + WookieepediaArticleModalService surface
├── checklists/
│   └── requirements.md  # Spec quality checklist (already shipped)
└── tasks.md             # Phase 2 output (/speckit-tasks — NOT created by this command)
```

### Source Code (repository root)

This feature is **Frontend-only** with one test-project addition. The repo follows the Architecture-Constraints folder convention from the constitution (`Features/<FeatureName>/` for vertical slices, `<FeatureName>/` for cross-cutting). The Wookieepedia modal is cross-cutting (used by SP-4 anywhere) so it lands under `Components/Shared/` and `Services/`, not in a `Features/<FeatureName>/` slice.

```text
src/StarWarsData.Frontend/
├── Components/
│   └── Shared/
│       ├── CopilotSidebar.razor          # EDIT: merge GlobalCopilotTools.Tools into ChatOptions.Tools
│       ├── CopilotSidebar.razor.cs       # EDIT: inject IGlobalCopilotTools, OnChange subscribe
│       ├── WookieepediaArticleDialog.razor       # NEW
│       ├── WookieepediaArticleDialog.razor.cs    # NEW
│       └── WookieepediaArticleDialog.razor.css   # NEW (scoped)
├── Services/
│   ├── PageControlService.cs              # UNTOUCHED (per-page actions stay there)
│   ├── GlobalCopilotToolsService.cs       # NEW (sibling to PageControlService)
│   ├── WookieepediaArticleModalService.cs # NEW (single-modal-instance semantics)
│   ├── WookieepediaUrlBuilder.cs          # NEW (pure-logic URL construction; unit-testable)
│   └── GlobalFilterService.cs             # UNTOUCHED (read-only consumer)
└── Program.cs                              # EDIT: DI registration for the two new services

src/StarWarsData.Services/
└── AI/
    └── Agents/
        └── CopilotAgent.cs                # EDIT: InstructionsTemplate gains "GLOBAL SP-4 TOOLS" block

src/StarWarsData.Tests/
└── Unit/
    └── Frontend/
        └── WookieepediaUrlBuilderTests.cs  # NEW (pure-logic; [TestCategory(TestTiers.Unit)])

specs/
└── 043-sp4-global-tool-family/spec.md          # NEW (graduates the sp4_* convention)

CLAUDE.md                                   # EDIT: reference 043 in the design-doc index
```

**Structure Decision**: **Web** (Frontend-only). All new files land in the Frontend project's existing `Components/Shared/` and `Services/` folders, matching the cross-cutting placement of the existing `CopilotSidebar.razor` and `PageControlService.cs` (Design-022, Design-041). No new feature folder is needed because the modal is not a vertical slice — it is a shared capability owned by SP-4 itself.

### Post-Phase-1 Constitution Re-check

After authoring `research.md`, `data-model.md`, `quickstart.md`, and the three contracts:

- **I. Library-First** — ✅ Re-confirmed. Research found no MudBlazor deviation needed: `MudDialog`'s `FullScreen` parameter + `DialogOptions.FullScreen` covers the `<960px` mobile fullscreen path without bespoke CSS.
- **II. Production Data Safety** — ✅ Re-confirmed. No write paths surfaced in design.
- **III. Test Tiering** — ✅ Re-confirmed. `WookieepediaUrlBuilderTests.cs` is the only new test file; it has zero external dependencies.
- **IV. UI Validated in Browser** — ✅ Gated. `quickstart.md` enumerates the five Chrome DevTools MCP exercises; `/speckit-implement` MUST perform them.
- **V. Engineering Docs in Sync** — ✅ Gated. `contracts/instructions-delta.md` records the precise prompt block to inline into `CopilotAgent.cs`, and the new `specs/043-sp4-global-tool-family/spec.md` is a tasked-out implementation deliverable (in `tasks.md` once generated).
- **VI. KG-First** — ✅ Re-confirmed. The pageId→title path uses the existing entity-lookup endpoint, which already reads `kg.nodes`. No new MongoDB code in this feature.
- **VII. Global Filter Respect** — ✅ Re-confirmed. `service-contract.md` documents the `GlobalFilterService.Continuity` read at modal-open time and the explicit non-auto-reload behaviour for mid-read filter changes.

**Result**: ✅ All gates re-pass after Phase 1. No Complexity Tracking entries needed.

## Complexity Tracking

> No entries — all seven constitutional gates pass without justification at both the pre-Phase-0 and post-Phase-1 checks.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| *(none)*  | *(n/a)*    | *(n/a)*                              |
