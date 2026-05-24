# Implementation Plan: Family Tree Component

**Branch**: `feature/042-family-tree-component` | **Date**: 2026-05-24 | **Spec**: [specs/042-family-tree-component/spec.md](./spec.md)

**Input**: Feature specification from `/specs/042-family-tree-component/spec.md`

## Summary

Add a dedicated family-tree visualization to the Ask page so the agent can answer kinship questions ("Skywalker family tree centered on Anakin") with a marriage-aware, generation-aligned renderer instead of the generic force/tree `render_graph`. The renderer is the vendored [donatso/family-chart-premium](https://github.com/donatso/family-chart-premium) library (non-commercial grant — covers this independent fan project, see [spec.md § Library choice](./spec.md#library-choice-donatsofamily-chart-premium)). A new `render_family_tree` AI tool emits a `FamilyTreeDescriptor`; a new `GET /api/RelationshipGraph/family-tree/{pageId}` endpoint runs a kinship-filtered BFS over `kg.edges` and projects to the renderer's `{people[], kinship[], limitations}` shape; a new `FamilyTreeView.razor` + `wwwroot/js/family-tree.js` pair wraps the chart with the same lifecycle/disposal pattern as [GraphViewer.razor](../../src/StarWarsData.Frontend/Components/Shared/GraphViewer.razor). The premium build's `spouse-link-text` and `kinship` plugins close two Open Questions from the prior MIT-version plan (partner-vs-spouse rendering, `has_relative` ambiguity).

## Technical Context

**Language/Version**: C# 13 with preview features + nullable enabled (per repo convention), targeting .NET 10. JavaScript ES2020+ for the vendored UMD library and the `wwwroot/js/family-tree.js` interop wrapper.

**Primary Dependencies**:

- [donatso/family-chart-premium](https://github.com/donatso/family-chart-premium) — vendored UMD (`dist/family-chart.js`, `buildName: "family-chart"` so the runtime global stays `f3`), pinned to a specific commit SHA while upstream is `0.0.0-beta.x`. Non-commercial licence; `LICENSE.txt` and `VERSION.txt` (commit SHA) live alongside the bundle. JS API identical to the MIT [donatso/family-chart](https://github.com/donatso/family-chart) — the documented fallback if the project ever takes commercial activity.
- `d3@7` — already in `wwwroot/lib/` via `d3.v7.min.js` referenced by `App.razor` (peer dep satisfied).
- MudBlazor 9.4.0 — used as the page chrome (`MudPaper`, `MudChip`, `MudAlert`) around the chart `<div>` mount.
- Microsoft.Extensions.AI + Microsoft.Agents.AI + OpenAI SDK — existing AI stack. No Semantic Kernel (Architecture Constraint).
- MongoDB.Driver — read against `kg.nodes`/`kg.edges` and `raw.pages.infobox` (for the `Gender` field gap).

**Storage**: MongoDB `starwars-dev` (default `Settings.DatabaseName`). Reads only — no writes from this feature. Production target = `starwars-prod` via deploy-time override.

**Testing**: MSTest on Microsoft.Testing.Platform, three-tier per Principle III:

- **Unit** — `BuildFamilyTreeAsync` projection rules against the `ApiFixture` seed (Skywalker / Solo / Naberrie / Lars); bidirectional-linking repair; synthetic-stub emission; `limitations` flag wiring. No Docker, no network.
- **Integration** — `GET /api/RelationshipGraph/family-tree/{pageId}` end-to-end via Testcontainers MongoDB. Verifies wire shape, status codes, and continuity/realm passthrough.
- **Agent** — one test in `Agent/`: prompt "Show me the Skywalker family tree centered on Anakin", asserts the agent chooses `render_family_tree` (not `render_graph`) for kinship phrasing.

**Target Platform**: Blazor Interactive Server (.NET 10 preview) running on the Aspire AppHost in dev; Linux container in publish. Desktop browsers (≥ `md` / 960px) get the chart; below `md` the existing `MobileSummary` markdown pattern from [Design-011](../011-mobile-web-ux/spec.md) is rendered instead.

**Project Type**: Web application — extends the existing `StarWarsData.ApiService` (new controller method) + `StarWarsData.Frontend` (new component + vendored JS) + `StarWarsData.Services` (new projection method + descriptor + tool) + `StarWarsData.Tests` (Unit + Integration + Agent additions).

**Performance Goals**:

- BFS projection (`BuildFamilyTreeAsync`) ≤ 200 ms p95 against `starwars-dev` for `maxDepth ≤ 5` and `maxNodes ≤ 200`. Mirrors the existing `QueryGraphAsync` budget.
- First paint of the rendered tree ≤ 500 ms after the descriptor lands, for trees of ≤ 200 people. Validated visually on the Skywalker tree.
- JS bundle size impact: vendored `family-chart.js` UMD ≤ 200 KB minified, loaded only on `/ask` (no impact on cold-start of other pages).

**Constraints**:

- **Non-commercial licence** (Principle I — library deviations / [spec.md § Library choice](./spec.md#library-choice-donatsofamily-chart-premium)). Watermark / license-key validation in the free build MUST NOT be circumvented per upstream restriction #2. If a watermark surfaces in the rendered tree, accept it; do not strip.
- **Read-only renderer**. `wwwroot/js/family-tree.js` MUST NOT invoke `.editTree()`. Editing surfaces are out of scope (Non-goal in spec).
- **No CDN at runtime** — bundle is fully vendored. Same convention as `d3-graph.js`.
- **KG-First** (Principle VI). All non-gender data comes from `kg.nodes`/`kg.edges`. The one `raw.pages.infobox.Data` read is for the `Gender` field — kg.nodes does not surface this and the family-chart data shape requires `"M"|"F"`. Document this gap in `research.md`; the long-term fix is an ETL change to surface `gender` on the Character node (out of scope here).
- **Global Filter** (Principle VII). Endpoint accepts `continuity` and `realm` query params and applies them identically to `QueryGraphAsync`. `FamilyTreeView.razor` subscribes to `GlobalFilterService.OnChange` and re-fetches on filter change. Continuity badge on the rendered chart uses the canonical theme colours (Canon → `Color.Primary`, Legends → `Color.Secondary`).

**Scale/Scope**: One new descriptor record, one new controller method, one new service method, one new Razor component, one new JS interop module, one new vendored asset folder. ~6 unit tests, ~3 integration tests, 1 agent test. Spec, plan, research.md, data-model.md, contracts/, quickstart.md, and (later) tasks.md.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Note |
| --- | --- | --- |
| **I. Library-First, Deviations Documented** | PASS | family-chart-premium is consumed via its public `f3.createChart` API. No bespoke wrapper, no API replacement. Vendoring under `wwwroot/lib/family-chart-premium/` is a packaging choice (no-CDN-at-runtime), not a deviation from the library's public API — no ADR-004 entry needed. The licence terms are captured in [spec.md § Library choice](./spec.md#library-choice-donatsofamily-chart-premium). If we ever vendor a *second* non-NuGet JS dep, that codifies a convention worth a new ADR (flagged in spec § Revisit when). |
| **II. Production Data Safety (NON-NEGOTIABLE)** | PASS | All reads target `kg.nodes`/`kg.edges` and `raw.pages.infobox` in the default DB (`starwars-dev`). No writes. Integration tests use Testcontainers MongoDB — never live prod. Agent tests use `starwars-dev`. |
| **III. Test Tiering & Pre-Commit Gate** | PASS | New tests slot into the three tiers exactly: projection logic → Unit; endpoint behaviour → Integration; agent tool-choice → Agent. Unit tier stays Docker-free and OpenAI-free. |
| **IV. UI Changes Validated in a Browser (NON-NEGOTIABLE)** | PASS — gated at report-back | `FamilyTreeView.razor` is a new rendered surface. Validation loop is mandatory: navigate to `/ask`, prompt the Skywalker family tree, snapshot the DOM, screenshot the rendered chart, resize to 414×896 to confirm `MobileSummary` fallback, read the console for Blazor circuit drops / JS interop errors. See [quickstart.md](./quickstart.md) for the exact MCP recipe. |
| **V. Engineering Docs Stay in Sync** | PASS | [spec.md](./spec.md) was amended in this same branch (commit `1041d6ed42`) to reflect the family-chart-premium switch. Plan + research.md + data-model.md + contracts/ + quickstart.md land in this PR. No `eng/diagrams` change (no architecture-shape change). No new ADR (see Principle I row). |
| **VI. KG-First Data Access at Runtime** | PASS with documented gap | All structural data (`name`, `wikiUrl`, `imageUrl`, kinship edges) comes from `kg.nodes`/`kg.edges`. The `Gender` field is read from `raw.pages.infobox.Data` because `kg.nodes` does not surface gender. This is soft-handled (missing/ambiguous → `"M"` + entry in `limitations.missingGenders`) and the proper fix — an ETL change to project gender into `kg.nodes` — is recorded in research.md as a follow-up. Per Principle VI this is "data genuinely absent at source"; the gap is the surface, not the projection. |
| **VII. Global Filter Respect** | PASS | Endpoint accepts `continuity` + `realm`; `FamilyTreeView.razor` subscribes to `GlobalFilterService.OnChange`; the continuity badge on the rendered chart uses canonical theme colours. The active filter is recorded on the response so re-renders survive replay. |

**Re-evaluation after Phase 1**: passes — no new violations surface in data-model / contracts. The Gender source-of-truth gap remains the only documented soft-edge; it is bounded (one field, one page type, with a logged limitations entry) and has a recorded follow-up.

## Project Structure

### Documentation (this feature)

```text
specs/042-family-tree-component/
├── spec.md              # Feature spec (already shipped; amended in this branch)
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output — C# records + Mongo projection mapping
├── quickstart.md        # Phase 1 output — dev verification recipe
├── contracts/
│   ├── family-tree-endpoint.md   # HTTP contract for GET /api/RelationshipGraph/family-tree/{pageId}
│   └── render-family-tree-tool.md # AI tool contract for render_family_tree
└── tasks.md             # Phase 2 output (generated by /speckit-tasks, NOT this command)
```

### Source Code (repository root)

```text
src/
├── StarWarsData.ApiService/
│   └── Features/KnowledgeGraph/
│       └── RelationshipGraphController.cs           # new endpoint method
├── StarWarsData.Models/
│   └── AI/
│       └── Ask.cs                                   # add FamilyTreeDescriptor (sibling of existing descriptors)
├── StarWarsData.Services/
│   ├── AI/
│   │   ├── Agents/AskAIAgent/
│   │   │   └── AskAIAgent.cs                        # add prompt routing block for render_family_tree
│   │   └── Toolkits/
│   │       └── ChartToolKit.cs                      # add render_family_tree tool
│   └── KnowledgeGraph/
│       └── KnowledgeGraphQueryService.cs            # add BuildFamilyTreeAsync
└── StarWarsData.Frontend/
    ├── Components/Shared/
    │   └── FamilyTreeView.razor                     # new component
    ├── wwwroot/
    │   ├── js/
    │   │   └── family-tree.js                       # new JS interop module
    │   └── lib/
    │       └── family-chart-premium/
    │           ├── family-chart.js                  # vendored UMD bundle
    │           ├── styles/                          # vendored CSS
    │           ├── LICENSE.txt                      # required by upstream notice
    │           └── VERSION.txt                      # one line, upstream commit SHA

tests/
└── StarWarsData.Tests/
    ├── Unit/
    │   └── FamilyTreeProjectionTests.cs             # ApiFixture seed; projection rules; bidirectional repair
    ├── Integration/
    │   └── FamilyTreeEndpointTests.cs               # Testcontainers Mongo; wire shape; continuity/realm
    └── Agent/
        └── FamilyTreeAgentRoutingTests.cs           # live agent picks render_family_tree for kinship phrasing
```

**Structure Decision**: Existing five-project layout per `Settings.DatabaseName` ([CLAUDE.md § Architecture](../../CLAUDE.md)). No new project. Feature-folder convention preserved: ApiService work goes under `Features/KnowledgeGraph/`, AI work under `Services/AI/`, frontend under `Components/Shared/` + `wwwroot/`. Vendored library follows the same shape as `wwwroot/lib/` siblings.

## Dependencies on prior specs and ADRs

Binding constraints carried into this plan:

- **[ADR-004 — Deviations from Standard MudBlazor Components](../../eng/adr/004-mudblazor-deviations.md)** — the principle that library deviations need documentation. Cited here to confirm vendoring is not such a deviation (the library *is* the public API; we wrap it in MudPaper but don't replace any MudBlazor primitive).
- **[Design-022 — Page-Aware Copilot Sidebar](../022-galaxy-map-copilot/spec.md)** — the grounding pattern reused for the Ask routing: the agent receives the page context and the user's prompt; the routing block in `AskAIAgent.cs` extends the same prompt vocabulary. SP-4 (sidebar) does *not* call `render_family_tree` — that tool is Ask-only because the rendered chart needs the larger surface (Design-022 § Toolkit explicitly excludes `render_*` from the sidebar). The agent prompt teaches SP-4 to suggest the user open `/ask` for family-tree questions if they arrive in the sidebar.
- **[Design-011 — Mobile Web UX](../011-mobile-web-ux/spec.md)** — the `<md` gate (960 px) and the `MobileSummary` markdown fallback for surfaces that don't render on touch.
- **[Design-036 — Path Graph Rendering](../036-path-graph-rendering/spec.md)** — the existing path-graph viewer used as the structural sibling for `FamilyTreeView.razor` (same lifecycle pattern, same `IAsyncDisposable`, same `DotNetObjectReference` callback shape).
- **[Design-041 — SP-4 Page Control AGUI Frontend Tools](../041-sp-4-page-control-agui-frontend-tools/spec.md)** — the `render_*` tools live in the Ask agent's toolkit, not the SP-4 sidebar's. This plan respects that boundary.

## Complexity Tracking

No constitution violations to justify — the table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| *(none)* | — | — |
