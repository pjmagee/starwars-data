---
description: "Task list for 042-family-tree-component implementation"
---

# Tasks: Family Tree Component

**Input**: Design documents from [specs/042-family-tree-component/](./)

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md) — all committed at `7bccc1af4c`.

**Tests**: Included per Principle III (Test Tiering & Pre-Commit Gate) — Unit + Integration + Agent tier tasks are first-class items, not optional.

**Organization**: Tasks are grouped by three user stories matching the plan's phases. Each story is independently testable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Parallelisable — different files, no dependency on incomplete prior tasks.
- **[US1]/[US2]/[US3]**: User-story label; setup, foundational, and polish phases carry no story label.
- Every task names the exact file path it touches and (where relevant) the constitution principle it engages.

## Path Conventions

Five-project .NET solution per [plan.md § Project Structure](./plan.md#project-structure):

- Models: `src/StarWarsData.Models/`
- Services: `src/StarWarsData.Services/`
- ApiService: `src/StarWarsData.ApiService/`
- Frontend: `src/StarWarsData.Frontend/`
- Tests: `src/StarWarsData.Tests/Unit/`, `…/Integration/`, `…/Agent/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Pre-flight checks against the existing repo before code lands.

- [ ] T001 Verify the active feature branch is `feature/042-family-tree-component` and the working tree is clean (no uncommitted plan/research drift) via `git status` + `git branch --show-current`.
- [ ] T002 [P] Confirm `ApiFixture` (`src/StarWarsData.Tests/Infrastructure/ApiFixture.cs`) seed already contains kg.nodes + kg.edges for the Skywalker / Solo / Naberrie / Lars families per [data-model.md § Edge cases](./data-model.md#edge-cases-captured-in-unit-tests). If any fixture node is missing (e.g. Han Solo or Ben Solo), extend the seed in the same file. Principle III.
- [ ] T003 [P] Confirm `d3.v7.min.js` is already referenced from `src/StarWarsData.Frontend/Components/App.razor` (peer dep for family-chart-premium per [research.md R-1](./research.md#r-1-family-chart-premium-dist-shape-and-entry-point)). No new task if present; otherwise file an issue and pause.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Data records shared by every story. Endpoint, AI tool, and Razor component all consume these.

**⚠️ CRITICAL**: No US1/US2/US3 task can begin until this phase is complete.

- [ ] T004 [P] Create `FamilyTreePersonData` record with `[JsonPropertyName("first name")]` / `[JsonPropertyName("last name")]` per [data-model.md § FamilyTreePerson](./data-model.md#familytreeperson) in `src/StarWarsData.Models/AI/Ask.cs`.
- [ ] T005 [P] Create `FamilyTreeRels` record (nullable `Parents`/`Spouses`/`Children` string lists) in `src/StarWarsData.Models/AI/Ask.cs`.
- [ ] T006 [P] Create `FamilyTreeKinshipEntry` record (`PersonId`, `RelativeId`, `Relationship`) per [data-model.md § FamilyTreeKinshipEntry](./data-model.md#familytreekinshipentry-premium-kinship-plugin) in `src/StarWarsData.Models/AI/Ask.cs`.
- [ ] T007 [P] Create `FamilyTreeLimitations` record (`MissingGenders`, `AdoptiveRelationsExcluded`, `TruncatedAtDepth`, `CycleFallback`) per [data-model.md § FamilyTreeLimitations](./data-model.md#familytreelimitations) in `src/StarWarsData.Models/AI/Ask.cs`.
- [ ] T008 Create `FamilyTreePerson` record composing T004 + T005 in `src/StarWarsData.Models/AI/Ask.cs` (depends on T004, T005).
- [ ] T009 Create `FamilyTreeResponse` record (`RootId`, `RootName`, `People`, `Kinship?`, `Limitations`) — endpoint wire shape per [contracts/family-tree-endpoint.md § Response](./contracts/family-tree-endpoint.md#response) in `src/StarWarsData.Models/AI/Ask.cs` (depends on T006, T007, T008).
- [ ] T010 Create `FamilyTreeDescriptor` record wrapping the response with the Ask envelope (`Title`, `RootEntityId`, `RootEntityName`, `MaxDepth`, `Continuity`, `MobileSummary`, `References?`) per [data-model.md § FamilyTreeDescriptor](./data-model.md#familytreedescriptor) in `src/StarWarsData.Models/AI/Ask.cs` (depends on T009).

**Checkpoint**: Foundation ready — US1/US2/US3 can now proceed.

---

## Phase 3: User Story 1 — Server projects a family tree from the KG (Priority: P1) 🎯 MVP

**Goal**: A clean call to `GET /api/RelationshipGraph/family-tree/{pageId}?maxDepth=3&continuity=Canon` returns the marriage-aware, generation-aware family tree for the focal Character, projected from `kg.nodes` + `kg.edges` + the Gender slot from `raw.pages.infobox.Data`. The `render_family_tree` AI tool wraps this endpoint and returns a `FamilyTreeDescriptor`.

**Independent Test**: Use the curl recipe in [quickstart.md § 3](./quickstart.md#3-smoke-the-endpoint-directly-phase-1-done) against `starwars-dev` with Anakin Skywalker's PageId. Verify the wire shape, the bidirectional-link invariant, and continuity passthrough. Independently of any UI.

### Tests for User Story 1 (Principle III — Unit tier, write FIRST)

- [ ] T011 [P] [US1] Create `src/StarWarsData.Tests/Unit/FamilyTreeProjectionTests.cs` scaffold with `[TestClass]`, `[TestCategory(TestTiers.Unit)]`, and `[ClassInitialize]` wiring to `ApiFixture`. Principle III.
- [ ] T012 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: Anakin root projects with `Rels.Spouses=[Padmé.Id]`, `Rels.Parents=[Shmi.Id]`, `Rels.Children=[Luke.Id, Leia.Id]` per [data-model.md § Edge cases](./data-model.md#edge-cases-captured-in-unit-tests).
- [ ] T013 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: bidirectional spouse repair — seed has only `(Anakin, partner_of, Padmé)`, output has BOTH `Anakin.Spouses=[Padmé.Id]` AND `Padmé.Spouses=[Anakin.Id]`.
- [ ] T014 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: parent↔child symmetry — if `personA.Parents` includes B, then `personB.Children` includes A.
- [ ] T015 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: truncation at `maxNodes` emits synthetic stubs with `Id == "{pageId}-stub"` and `Data.PageId == realPageId`; sets `Limitations.TruncatedAtDepth=true` per [research.md R-7](./research.md#r-7-synthetic-stub-navigability).
- [ ] T016 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: missing Gender on a Character → `Data.Gender="M"` AND PageId appended to `Limitations.MissingGenders` per [research.md R-5](./research.md#r-5-gender-field-source-of-truth-kg-first-principle-vi-gap). Principle VI.
- [ ] T017 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: adoptive (Leia ↔ Organa family membership, no `parent_of` to Bail) → Bail NOT in `Leia.Rels.Parents` AND entry in `Limitations.AdoptiveRelationsExcluded`.
- [ ] T018 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: `has_relative` edge emits `Kinship[]` entry only — never appears in `Parents`/`Spouses`/`Children`.
- [ ] T019 [P] [US1] Unit test in `FamilyTreeProjectionTests.cs`: `maxDepth` clamping `[1..5]` — values 0, -1, 99 all clamp silently to the boundary; clamping is enforced in `BuildFamilyTreeAsync`, not at controller bind.

### Implementation for User Story 1

- [ ] T020 [US1] Add `RenderFamilyTree = "render_family_tree"` constant to `src/StarWarsData.Services/AI/ToolNames.cs`. Principle I.
- [ ] T021 [US1] Implement `BuildFamilyTreeAsync(int rootId, int maxDepth, string? continuity, string? realm, int maxNodes = 200, CancellationToken ct)` in `src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs` — six steps per [data-model.md § Mongo projection rules](./data-model.md#mongo-projection-rules): BFS, project to person records, kinship entries, family-membership handling, bidirectional repair (incl. synthetic stubs), truncation. Reads `kg.nodes` + `kg.edges` for structure and `raw.pages.infobox.Data` for the Gender slot only. Principle VI (KG-First with documented gap).
- [ ] T022 [US1] Add `GET /api/RelationshipGraph/family-tree/{pageId}` action method in `src/StarWarsData.ApiService/Features/KnowledgeGraph/RelationshipGraphController.cs` accepting `maxDepth`, `continuity`, `realm` query params; calls `BuildFamilyTreeAsync`; returns the `FamilyTreeResponse` JSON per [contracts/family-tree-endpoint.md](./contracts/family-tree-endpoint.md). Principle VII (Global Filter passthrough). Depends on T021.
- [ ] T023 [US1] Reject non-Character root with `400 Bad Request` body `{ "error": "RootMustBeCharacter", "actualType": "<type>" }` per [contracts/family-tree-endpoint.md § Error responses](./contracts/family-tree-endpoint.md#error-responses) in the same controller method (depends on T022).
- [ ] T024 [US1] Reject invalid `continuity` / `realm` query values with `400 Bad Request` body `{ "error": "InvalidQueryParameter", "name": "<n>", "value": "<v>" }` in the same controller method (depends on T022).
- [ ] T025 [US1] Return `404 Not Found` body `{ "error": "RootNotFound", "pageId": <id> }` when `pageId` is absent from `kg.nodes` (depends on T022).
- [ ] T026 [US1] Implement `render_family_tree` tool in `src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs` returning a `FamilyTreeDescriptor`. Tool description verbatim from [contracts/render-family-tree-tool.md § Tool description](./contracts/render-family-tree-tool.md#tool-description-llm-facing). Calls the endpoint from T022 internally; validates `rootEntityId` resolves to a Character before calling. Depends on T020, T010, T022.
- [ ] T027 [US1] Register `render_family_tree` in `AskAIAgent`'s tool collection alongside `render_graph` / `render_path` in `src/StarWarsData.Services/AI/Agents/AskAIAgent/AskAIAgent.cs` (depends on T026).

### Integration tests for User Story 1 (Principle III — Integration tier)

- [ ] T028 [P] [US1] Create `src/StarWarsData.Tests/Integration/FamilyTreeEndpointTests.cs` scaffold with `[TestClass]`, `[TestCategory(TestTiers.Integration)]`, Testcontainers MongoDB fixture per existing repo pattern. Principle III.
- [ ] T029 [P] [US1] Integration test in `FamilyTreeEndpointTests.cs`: `GET /api/RelationshipGraph/family-tree/{anakinPageId}` returns `200`, `Content-Type: application/json`, body matches the wire-shape contract (rootId, rootName, people[], limitations).
- [ ] T030 [P] [US1] Integration test in `FamilyTreeEndpointTests.cs`: `?continuity=Canon` filters edges — no edge in the response was tagged Legends in the seed.
- [ ] T031 [P] [US1] Integration test in `FamilyTreeEndpointTests.cs`: non-Character root (e.g. `Skywalker family` aggregate's PageId) returns `400 RootMustBeCharacter`.
- [ ] T032 [P] [US1] Integration test in `FamilyTreeEndpointTests.cs`: bidirectional-link invariant holds across every spouse / parent / child reference in the response (verified by walking the response, not by trusting projection).

**Checkpoint**: US1 fully functional. Curl smoke test passes; Unit + Integration tiers green. Deployable as a backend-only MVP — the AI agent can produce family-tree descriptors that any client can consume.

---

## Phase 4: User Story 2 — Frontend renders the family tree in Ask (Priority: P2)

**Goal**: On `/ask`, asking "Show me the Skywalker family tree centered on Anakin Skywalker" produces a rendered marriage-aware tree inside a `MudPaper`. Below the 960 px `md` gate the rendered tree is hidden and the `MobileSummary` markdown bullets appear instead.

**Independent Test**: Run [quickstart.md § 5](./quickstart.md#5-chrome-devtools-mcp-ui-validation-principle-iv--non-negotiable) — navigate to `/ask`, fire the prompt, snapshot the DOM, screenshot the chart, resize to 414×896 and confirm the mobile fallback. Independently of agent-routing correctness (agent fed the descriptor by hand if needed).

### Implementation for User Story 2

- [ ] T033 [P] [US2] Vendor family-chart-premium per [quickstart.md § 1](./quickstart.md#1-vendor-family-chart-premium) — `gh api` recipe pulls `dist/family-chart.js` + `dist/styles/` + `LICENSE.txt` from the upstream main branch commit SHA into `src/StarWarsData.Frontend/wwwroot/lib/family-chart-premium/`. Also write `VERSION.txt` containing the upstream commit SHA. Principle I (library deviations — licence terms retained verbatim per restriction #3).
- [ ] T034 [US2] Add `<link rel="stylesheet" href="lib/family-chart-premium/styles/family-chart.css" />` and `<script src="lib/family-chart-premium/family-chart.js"></script>` to the `<head>` of `src/StarWarsData.Frontend/Components/App.razor`, alongside the existing d3 reference (depends on T033).
- [ ] T035 [P] [US2] Create `src/StarWarsData.Frontend/wwwroot/js/family-tree.js` — ES module exporting `renderFamilyTree(containerId, data, dotNetRef)` and `destroy(containerId)`. Uses `window.f3.createChart`, opts into the `spouse-link-text` and `kinship` plugins per [research.md R-2](./research.md#r-2-premium-plugins-spouse-link-text-kinship--packaged-or-separate-imports), wires the card click handler to `dotNetRef.invokeMethodAsync('OnPersonClicked', pageId)`. **MUST NOT** invoke `.editTree()` — the renderer is read-only (Non-goal in spec).
- [ ] T036 [P] [US2] Create `src/StarWarsData.Frontend/Components/Shared/FamilyTreeView.razor` — structural sibling of `AskGraphView.razor`. `MudPaper` wrapper with title chip, continuity badge using canonical theme colours (Canon → `Color.Primary`, Legends → `Color.Secondary`; Principle VII), an advisory `MudAlert` chip when `Limitations` has any flag, and a `<div id="family-tree-@instanceId" class="f3" style="width:100%;height:720px;"></div>` for the chart mount. Loading + error states match `AskGraphView.razor` for visual consistency.
- [ ] T037 [US2] Add the mobile fallback (`d-block d-md-none`) inside `FamilyTreeView.razor` rendering the `MobileSummary` markdown via `MudMarkdown` per Design-011 (depends on T036).
- [ ] T038 [US2] Add `IAsyncDisposable`, `DotNetObjectReference<FamilyTreeView> _self`, and `[JSInvokable] OnPersonClicked(int pageId)` to `FamilyTreeView.razor` — the click handler routes to `NavigationManager` for `/knowledge-graph/nodes/{pageId}` (depends on T036, T035). Synthetic stubs carry their real PageId so the click still navigates per [research.md R-7](./research.md#r-7-synthetic-stub-navigability).
- [ ] T039 [US2] Load the JS module via `JS.InvokeAsync<IJSObjectReference>("import", "./js/family-tree.js")` in `OnAfterRenderAsync` first-render of `FamilyTreeView.razor` per the [GraphViewer.razor:369-479](../../src/StarWarsData.Frontend/Components/Shared/GraphViewer.razor#L369-L479) pattern; disposal releases both references (depends on T038).
- [ ] T040 [US2] Subscribe `FamilyTreeView.razor` to `GlobalFilterService.OnChange` to re-fetch the endpoint when continuity / realm changes (Principle VII; depends on T036).
- [ ] T041 [US2] Wire `src/StarWarsData.Frontend/Components/Pages/Ask.razor` to dispatch `FamilyTreeDescriptor` → `FamilyTreeView.razor`, sibling of the existing `GraphDescriptor` / `PathDescriptor` dispatch branches (depends on T036).

**Checkpoint**: US2 fully functional. Chrome DevTools MCP validation (Phase 6) confirms rendered tree, mobile fallback, continuity badge colour, console clean.

---

## Phase 5: User Story 3 — Agent routes kinship questions to render_family_tree (Priority: P3)

**Goal**: When the user prompts a kinship question ("family tree", "lineage", "ancestry", "trace heritage"), the Ask agent picks `render_family_tree` — not `render_graph` — and obeys the REQUIRED PRECONDITION (search_entities first, verify Character type).

**Independent Test**: Run the Agent-tier test against a live agent + `starwars-dev`. Multiple prompt phrasings; agent's tool call (visible in `FunctionCallContent`) is `render_family_tree`. Independent of whether US2's UI is wired (the agent test reads the tool call from the transcript, not from the rendered DOM).

### Implementation for User Story 3

- [ ] T042 [US3] Add the FAMILY-TREE ROUTING block to the `AskAIAgent` system prompt in `src/StarWarsData.Services/AI/Agents/AskAIAgent/AskAIAgent.cs` per [contracts/render-family-tree-tool.md § Agent prompt routing](./contracts/render-family-tree-tool.md#agent-prompt-routing-askaiagent-system-prompt-addition) — above the existing GRAPH VISUALIZATION WORKFLOW block.
- [ ] T043 [P] [US3] Create `src/StarWarsData.Tests/Agent/FamilyTreeAgentRoutingTests.cs` with `[TestClass]`, `[TestCategory(TestTiers.Agent)]`, live OpenAI + `starwars-dev` fixture per existing Agent-tier pattern. Principle III.
- [ ] T044 [P] [US3] Agent test in `FamilyTreeAgentRoutingTests.cs`: prompt `"Show me the Skywalker family tree centered on Anakin Skywalker"` → assert the streamed updates contain a `FunctionCallContent` for `render_family_tree` and zero `FunctionCallContent` for `render_graph`.
- [ ] T045 [P] [US3] Agent test in `FamilyTreeAgentRoutingTests.cs`: prompt `"What is Anakin Skywalker's lineage?"` (different kinship phrasing) → same assertion as T044.
- [ ] T046 [P] [US3] Agent test in `FamilyTreeAgentRoutingTests.cs`: prompt `"Show me the Sith Order command structure"` → asserts the agent picks `render_graph` (Tree mode), NOT `render_family_tree` (anti-pattern boundary).

**Checkpoint**: US3 fully functional. All three Agent-tier tests pass when run manually. The kinship boundary against political/military hierarchies holds.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Principle IV browser validation, watermark + cycle probes, performance check, doc sync.

- [ ] T047 Run [quickstart.md § 5.1-5.4](./quickstart.md#51-navigate-and-trigger-the-agent) — Chrome DevTools MCP loop on `/ask`, prompt the Skywalker family tree, snapshot the DOM, screenshot to `specs/042-family-tree-component/screenshots/skywalker-tree-desktop.png`, list console messages (no errors), list network requests (200 on `/api/RelationshipGraph/family-tree/…`). Principle IV (NON-NEGOTIABLE).
- [ ] T048 [P] Run [quickstart.md § 5.3](./quickstart.md#53-mobile-fallback-check) — resize to 414×896, snapshot, screenshot to `screenshots/skywalker-tree-mobile.png`, confirm the chart `<div>` is hidden and `MobileSummary` markdown bullets render. Principle IV.
- [ ] T049 [P] Run [quickstart.md § 5.5](./quickstart.md#55-watermark--branding-probe-researchmd-r-3) — visually inspect the rendered tree for any watermark / "Powered by …" footer / license-key modal. Record findings to `screenshots/watermark-probe.md`. Do **NOT** patch the bundle to remove anything (per [research.md R-3](./research.md#r-3-watermark--license-key-behaviour-in-the-free-tier) + upstream restriction #2). Principle I.
- [ ] T050 [P] Run [quickstart.md § 5.6](./quickstart.md#56-cycle-probe-researchmd-r-6) — prompt "Show me Boba Fett's family tree", screenshot to `screenshots/boba-fett-tree.png`, confirm the clone-as-parent relationship renders without infinite loop or JS stack overflow. Principle IV.
- [ ] T051 Performance budget check — measure `BuildFamilyTreeAsync` latency against `starwars-dev` for Anakin Skywalker at `maxDepth=3`. Expectation per [plan.md § Performance Goals](./plan.md#technical-context): ≤ 200 ms p95. Record the measured p95 in a one-line note at the bottom of `quickstart.md` or in `screenshots/perf-probe.md`.
- [ ] T052 Run `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"` — confirm all FamilyTreeProjectionTests + previously-passing Unit tier green. Principle III pre-commit gate.
- [ ] T053 Run `dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit|TestCategory=Integration"` — confirm FamilyTreeEndpointTests pass alongside the rest of the Integration tier. Principle III CI gate.
- [ ] T054 Manually run the three Agent-tier tests in `FamilyTreeAgentRoutingTests.cs` via `dotnet test --project src/StarWarsData.Tests --filter "FullyQualifiedName~FamilyTreeAgentRouting"` against a live OpenAI key + `starwars-dev`. Principle III (Agent tier is manual). Record cost in a one-line note.
- [ ] T055 Add a row to the [CLAUDE.md § Authoritative References](../../CLAUDE.md#authoritative-references) table linking [specs/042-family-tree-component/spec.md](./spec.md) under `Family tree rendering` so future agents discover the tool exists and the routing rule. Principle V.
- [ ] T056 Update the spec's Status line from `Proposed` to `Shipped (2026-MM-DD, <commit-sha>)` once the branch is merged. Principle V.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: independent.
- **Phase 2 (Foundational)**: depends on Phase 1. Blocks every user-story phase.
- **Phase 3 (US1, P1 — MVP)**: depends on Phase 2.
- **Phase 4 (US2, P2)**: depends on Phase 2 (for the descriptor type) + at least T022 (endpoint) live so the Razor component has a real endpoint to call. Test-validating US2 also requires T026 (the tool) for a meaningful end-to-end prompt — but the component itself can be exercised with a hand-crafted descriptor.
- **Phase 5 (US3, P3)**: depends on T020 + T026 + T027 (render_family_tree tool must be registered before the agent can pick it).
- **Phase 6 (Polish)**: depends on US1+US2+US3 complete.

### User Story Dependencies

- **US1** is the MVP — produces a working endpoint + AI tool without any UI. Backend-only consumers (curl, an AI agent in headless mode) can use it immediately.
- **US2** depends on US1's endpoint + descriptor. Independent of US3 (the Razor component takes a `FamilyTreeDescriptor`; it doesn't care how the agent constructed it).
- **US3** depends on US1's tool registration. Independent of US2 (the agent-routing test inspects the tool-call event, not the DOM).

### Within Each User Story

- Tests are written and run in the appropriate tier; Unit + Integration are pre-commit / CI gated. Agent tier is manual.
- Models (Phase 2) before services (US1 T021).
- Services before endpoints (US1 T021 before T022).
- Endpoint before tool (US1 T022 before T026 — tool calls endpoint).
- Tool before agent registration (US1 T026 before T027).
- Tool registration before agent prompt routing (US1 T027 before US3 T042).

### Parallel Opportunities

- **Phase 2**: T004 / T005 / T006 / T007 can all run in parallel (each adds a record to `Ask.cs` — slight care needed if multiple agents edit the same file concurrently; in a single-agent session they're sequential but conceptually independent).
- **Phase 3 Unit tests (T011-T019)**: all run in parallel — each adds a `[TestMethod]` to the same `FamilyTreeProjectionTests.cs`, but they're independent assertions; serialize the writes but parallelise the conceptual work.
- **Phase 3 Integration tests (T028-T032)**: same shape as Unit tests, all in `FamilyTreeEndpointTests.cs`.
- **Phase 4**: T033 (vendoring) and T035 (JS module) and T036 (Razor component scaffold) are fully independent files — `[P]`. Wiring tasks (T034, T037-T041) sequence afterward.
- **Phase 5**: T043 (Agent test scaffold), T044, T045, T046 are independent test methods in one file — `[P]`.
- **Phase 6 (T048-T050)**: three independent Chrome DevTools MCP probes — fully `[P]`.

---

## Parallel Example: User Story 1 Unit tests

```pwsh
# After Phase 2 (Foundational records) lands, every Unit test for US1 can be written together:
#   - T011 — test class scaffold
#   - T012 — Anakin happy path
#   - T013 — bidirectional spouse repair
#   - T014 — parent↔child symmetry
#   - T015 — synthetic stub
#   - T016 — missing gender → "M" + Limitations
#   - T017 — adoptive → Limitations
#   - T018 — has_relative → Kinship[]
#   - T019 — maxDepth clamping

# Implementation (T021) then satisfies all of them in one pass.
```

---

## Implementation Strategy

### MVP First (US1 only)

1. Phase 1: Setup checks.
2. Phase 2: Foundational records.
3. Phase 3: Server-side projection + endpoint + tool registration + tests.
4. **STOP and VALIDATE**: curl smoke test from quickstart §3 passes; `dotnet test` Unit + Integration tiers green.
5. Optional: deploy/demo the agent producing a `FamilyTreeDescriptor` in JSON via a headless `/ask` call.

### Incremental Delivery

1. Setup + Foundational → records ready.
2. US1 → headless / API-consumer demo (MVP).
3. US2 → end-to-end browser demo with the Skywalker family tree.
4. US3 → live agent picks the right tool for kinship phrasing.
5. Polish → Chrome DevTools MCP validation + watermark probe + cycle probe + perf budget.

### Per-task discipline

- After each task, run the Unit tier (`dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"`) to confirm no regression. Pre-commit gate per Principle III.
- After every UI-touching task in Phase 4, open the running app in the browser via Chrome DevTools MCP and snapshot the relevant page. Don't batch Principle IV validation until the end.
- Commit after each logical task group (Foundational records, projection + tests, endpoint, tool, frontend vendoring, frontend component, agent routing, polish). The pre-commit hooks (csharpier, dotnet build, dotnet test) catch breakage early.

---

## Notes

- **[P]** tasks = different files OR independent test methods within a single file. In a single-agent session, parallelisability is a conceptual signal (no dependency on incomplete work), not literal concurrency.
- **[US1]/[US2]/[US3]** label maps task → user story for traceability when reviewing the PR.
- Each user story is independently completable and testable per the [plan.md § Project Structure](./plan.md#project-structure) layout.
- Tests in the appropriate tier MUST be green before report-back; Agent-tier tests are manual but MUST be run before the PR merges (cost recorded).
- Commit after each task or logical group; never bundle US1+US2 in one commit.
- Stop at any checkpoint to validate the story independently.
