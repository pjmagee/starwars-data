# Design-042: Family Tree Component

**Status:** Shipped — 2026-05-24. All five implementation phases on `feature/042-family-tree-component`: foundational records (`8c96f9a472`); server-side projection + endpoint + tool registration (`486370ce6e` + `51493912b3` + `640ca4eeff`); Gender-from-`kg.nodes.properties` Principle-VI refactor (`0ad44b1e08`); frontend MVP — vendored renderer + Razor + JS interop + Chrome DevTools MCP validation (`c30a7c213f`); agent prompt routing + 3 Agent-tier tests (`fa30661fe9`). All 312 Unit+Integration tests pass; the 3 Agent-tier tests are manual per Principle III (cost OpenAI dollars). Watermark "Family Chart (Free)" present in the free-tier build, kept per upstream restriction #2 — see [screenshots/watermark-probe.md](./screenshots/watermark-probe.md).
**Date:** 2026-05-22 (originally proposed); 2026-05-24 (shipped)
**Author:** Patrick Magee + Claude
**Related:** [Design-036 Path Graph Rendering](../036-path-graph-rendering/spec.md), [Design-011 Mobile Web UX](../011-mobile-web-ux/spec.md), [Design-041 SP-4 Page Control AGUI Frontend Tools](../041-sp-4-page-control-agui-frontend-tools/spec.md), [ADR-004 MudBlazor Deviations](../../eng/adr/004-mudblazor-deviations.md), [donatso/family-chart-premium](https://github.com/donatso/family-chart-premium)

## Problem

The Ask page renders kinship questions ("Skywalker family tree centered on Anakin", "Trace Leia's lineage") through `render_graph` — the same tool that handles every other knowledge-graph visualization. `render_graph` produces either a force-directed network or a top-down BFS tree from `kg.edges`. Neither layout is shaped like a family tree:

- Marriages aren't visually clustered — spouses end up as two arbitrary neighbours of a shared child, not as a paired unit above their kids.
- Generations don't align — the BFS depth from the focal node, not the genealogical generation, drives row placement. Anakin (depth 0) sits above Padmé (depth 1) instead of beside her.
- Half-siblings, step-parents, and divorces have no representation primitives — every edge is the same width and weight.
- The graph balloons whenever the agent picks a slightly off label set ([branch `feature/ask-agui-chat-client` shipped a narrow-labels guardrail](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs) but that only addresses the *labels* axis; the layout is still wrong even with a perfect label set).

Verified manually on 2026-05-22 against `https://localhost:7274/ask` ("Show me the Skywalker family tree centered on Anakin Skywalker"). Even with the post-fix narrow label set (`child_of`, `parent_of`, `sibling_of`, `partner_of`, `family`), the resulting `GraphLayoutMode.Tree` rendered Padmé as a child-row sibling of Luke/Leia rather than as Anakin's paired spouse.

## Goals

- A dedicated `render_family_tree` AI tool that the Ask agent routes kinship questions to, separate from `render_graph`.
- A purpose-built UI component that clusters spouses, aligns generations, and uses kinship-specific visual primitives.
- Server-side projection from `kg.edges` to the renderer's data shape, with unit tests covering Skywalker / Solo / Naberrie / Lars families.
- Mobile fallback via the existing markdown `MobileSummary` pattern (Design-011) for `<md` viewports — family trees are unusable on a touch device.
- Continuity + Realm filter passthrough, matching every other Ask descriptor.

## Non-goals

- Editing family trees from the UI. This is a research surface, not a genealogy app.
- Adoptive vs biological parent distinction — we do not have `adopted_by` in `kg.edges` today. Bail/Breha Organa show up via `family` membership only, not `parent_of`. Out of scope for v1.
- Gender inference from names. If the Character infobox has no `Gender`, we default and emit a TODO marker. We do not guess.
- Charts for organizations, governments, or military lineages — `render_graph` (Force or Tree mode) keeps its existing scope.

## Design

### Library choice: donatso/family-chart-premium

- **License:** custom non-commercial grant (upstream `LICENSE.txt`) — free for personal, educational, hobby, or non-commercial use. This project qualifies: independent fan project, no revenue, MIT-licensed itself; the Buy Me a Coffee tip jar in the AppBar is a donation channel, not commercial use. Commercial pivot triggers a Revisit (see below).
- **Distribution:** GitHub repo at [donatso/family-chart-premium](https://github.com/donatso/family-chart-premium); the published build artefact is `dist/family-chart.js` (UMD; `buildName: "family-chart"` in `package.json`, so the runtime global stays `f3`). Ships styles under `dist/styles/`. Requires `d3@7` as a peer (already met).
- **Version pin:** upstream is currently `0.0.0-beta.2`. Pin a specific commit SHA when vendoring rather than tracking `main`; revisit when upstream cuts a stable release.
- **JS API is the same** as MIT family-chart: `f3.createChart('#el', data).setCardHtml().setCardDisplay([...]); f3Chart.updateTree({initial: true})`. Read-only mode = do not invoke `.editTree()`. The server-side projection rules below are unchanged from the MIT-library plan.
- **Data shape** (unchanged from MIT family-chart): array of person objects:

  ```text
  { id: string,
    data: { gender: "M"|"F", "first name": string, "last name": string, ... },
    rels: { parents: string[], spouses: string[], children: string[] } }
  ```

  Bidirectional linking is mandatory — if `A.rels.spouses` includes `B`, then `B.rels.spouses` must include `A`.
- **Premium features we actually use** (each maps to an open question the MIT version couldn't answer):
  - **`spouse-link-text` plugin** — labels the line between spouses (`Partner` vs `Spouse`), which lets us **stop collapsing `partner_of` into `spouses`** and instead surface the distinction visually. Closes one of the Open Questions below.
  - **`kinship` engine plugin** — relation-aware secondary view ("show me Anakin's descendants only"; "highlight Leia's in-laws"). Removes the need for the speculative "Extended kin sidebar" we considered for `has_relative`.
  - **Tree-filtering + layout-options** — first-class `maxDepth` per direction (ancestors vs descendants separately) and switchable layout modes. Lets the `MaxDepth` descriptor field drive the renderer directly instead of pre-trimming server-side.
  - **Advanced card variants + dynamic card styling** — continuity badges, missing-gender placeholders, and the "synthetic stub" `Unknown Unknown` placeholder become a styled card variant rather than free-text in the data field.

### Tool: `render_family_tree`

New tool in [src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs), sibling of `render_graph` and `render_path`. Tool description follows the same opinionated pattern (REQUIRED PRECONDITION, HARD ANTI-PATTERN).

Required precondition:

1. `search_entities(query)` → resolve PageId.
2. Verify the resolved entity's `type === "Character"`. The tool is rejected (and emits a fallback markdown summary) for Family / Organization / Government roots — those are aggregates, not people, and have no genealogy to render.

Hard anti-patterns spelled out in the tool description:

- Calling `render_family_tree` for a `Family` aggregate node. Use `search_entities` to disambiguate and pick the specific Character root the user named.
- Calling `render_family_tree` for political hierarchies, military command chains, or organization rosters. Use `render_graph` (Tree mode) for those.
- Including `labels` / `enabledLabels` parameters at all — family labels are fixed server-side. Any client-supplied label list is ignored.

### Descriptor: `FamilyTreeDescriptor`

New descriptor in [src/StarWarsData.Models/AI/Ask.cs](../../src/StarWarsData.Models/AI/Ask.cs):

| Field            | Type                | Default | Notes |
| ---------------- | ------------------- | ------- | ----- |
| `Title`          | `string`            | —       | Same as other descriptors. |
| `RootEntityId`   | `int`               | —       | PageId of the focal Character. |
| `RootEntityName` | `string`            | —       | Display name. |
| `MaxDepth`       | `int`               | `3`     | Generations to expand in each direction (ancestors + descendants). Clamped to `[1, 5]` server-side. |
| `Continuity`     | `string?`           | `null`  | `"Canon"`, `"Legends"`, or null = all. |
| `MobileSummary`  | `string`            | —       | Required. Bullet markdown shown on `<md` viewports. |
| `References`     | `List<Reference>?`  | `null`  | Standard reference list. |

Deliberately NO `Labels` / `EnabledLabels`. Family edge labels are fixed (`parent_of`, `child_of`, `sibling_of`, `partner_of`, `family`, `spouse_of`, `married_to`, `has_relative`) and live in the server-side mapper. The agent does not get to widen them.

### API endpoint: `GET /api/RelationshipGraph/family-tree/{pageId}`

New endpoint on [src/StarWarsData.ApiService/Features/KnowledgeGraph/RelationshipGraphController.cs](../../src/StarWarsData.ApiService/Features/KnowledgeGraph/RelationshipGraphController.cs):

```
GET /api/RelationshipGraph/family-tree/{pageId}?maxDepth=3&continuity=Canon&realm=StarWars
```

Returns the family-chart-shaped JSON directly (NOT the generic `RelationshipGraphResult`):

```json
{
  "rootId": "452390",
  "rootName": "Anakin Skywalker",
  "people": [
    {
      "id": "452390",
      "data": {
        "gender": "M",
        "first name": "Anakin",
        "last name": "Skywalker",
        "wikiUrl": "https://starwars.fandom.com/wiki/Anakin_Skywalker",
        "imageUrl": "...",
        "pageId": 452390
      },
      "rels": {
        "parents": ["452391"],
        "spouses": ["452392"],
        "children": ["452393", "452394"]
      }
    },
    {
      "id": "452391",
      "data": { "gender": "F", "first name": "Shmi", "last name": "Skywalker Lars", ... },
      "rels": { "spouses": ["452395"], "children": ["452390"] }
    },
    {
      "id": "452392",
      "data": { "gender": "F", "first name": "Padmé", "last name": "Amidala", ... },
      "rels": { "spouses": ["452390"], "children": ["452393", "452394"] }
    },
    { "id": "452393", "data": { "gender": "M", "first name": "Luke", ... },
      "rels": { "parents": ["452390", "452392"] } },
    { "id": "452394", "data": { "gender": "F", "first name": "Leia", ... },
      "rels": { "parents": ["452390", "452392"] } }
  ],
  "limitations": {
    "missingGenders": [],
    "adoptiveRelationsExcluded": ["452394→Bail Prestor Organa"],
    "truncatedAtDepth": false
  }
}
```

The `limitations` block lets the frontend show a small advisory chip when the tree had to fudge something — it's metadata, not a renderer input.

### Server-side mapping rules

Implemented in [src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs) as a new `BuildFamilyTreeAsync(int rootId, int maxDepth, …)` method. Mirrors the BFS shape of `QueryGraphAsync` but with kinship-specific filtering and projection:

1. **BFS the family neighbourhood.** Outbound + inbound passes over `kg.edges`, label filter fixed to the family set (`parent_of`, `child_of`, `sibling_of`, `partner_of`, `family`, `spouse_of`, `married_to`, `has_relative`). Continuity + Realm filters applied identically to the existing query.
2. **Project to family-chart shape.** For each visited node:
   - `id` = PageId as string (family-chart requires string IDs).
   - `data.first name` / `data.last name` — split the kg.nodes `name` on the last whitespace; collision-prone names ("Boba Fett") accept that the split is imperfect.
   - `data.gender` — read `raw.pages.infobox.Data` for the `Gender` field on the Character template (see [FieldSemantics.cs:29](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs#L29)). Map `"Male"`→`"M"`, `"Female"`→`"F"`, anything else (including `"Non-binary"`, missing, ambiguous) → `"M"` + add the PageId to `limitations.missingGenders`. **This is a family-chart library constraint, not a model decision.** Do not propagate `"M"`/`"F"` anywhere else in the codebase.
   - `data.wikiUrl` / `data.imageUrl` / `data.pageId` — from kg.nodes for click-through.
   - `rels.parents` — every node `B` such that an edge `(A, parent_of, B)` exists OR an edge `(B, child_of, A)` exists (the reverse direction). Deduplicated.
   - `rels.children` — symmetric of parents.
   - `rels.spouses` — `partner_of` ∪ `spouse_of` ∪ `married_to`. Family-chart has no "partner vs spouse" distinction; collapse all three into `spouses` and emit a `limitations` note for entries that were `partner_of` only.
   - `family` membership edges — do NOT translate into a `parents` link. They become a sidebar "family memberships" list in the UI, not graph edges.
   - Adoptive relations — out of scope for v1. Where a Character is *only* connected to a parent via `family` membership of that parent's house (e.g. Leia ↔ Bail Organa via `Lars family` / `House of Organa`), record the relation in `limitations.adoptiveRelationsExcluded` for the advisory chip but do NOT emit it as `parents`.
3. **Enforce bidirectional linking.** After projection, scan every `(person, rels.spouses[])` pair and ensure the spouse's record lists the person back. Same for `parents`/`children`. Any orphan reference (e.g. parent referenced from a child, parent not in the result set) is repaired by emitting a synthetic stub `{id, data: {first name: "Unknown", last name: "Unknown", gender: "M"}, rels: {}}` and recording the PageId in `limitations.missingGenders`. Synthetic stubs are essential — family-chart crashes on dangling IDs.
4. **Truncate at maxDepth + maxNodes.** Same recall-biased cap as `QueryGraphAsync` (default `maxNodes=200`). Set `limitations.truncatedAtDepth=true` when hit.

Unit tests live alongside `RecordServiceTests` in [src/StarWarsData.Tests/Unit/](../../src/StarWarsData.Tests/Unit/) with the `ApiFixture` seed:

- Anakin → 1 spouse (Padmé), 2 children (Luke, Leia), 1 parent (Shmi), 1 step-parent excluded.
- Padmé → 0 parents in seed, 1 spouse, 2 children.
- Luke ↔ Leia as siblings via shared parents (no explicit `sibling_of` edge needed).
- Han Solo + Leia + Ben Solo as a separate family unit linked to Anakin's tree via Leia's spouse edge.
- Bidirectional linking: removing one half of a `spouses` pair from the projection still results in both halves present in the output.

### Frontend: `FamilyTreeView.razor`

New component at [src/StarWarsData.Frontend/Components/Shared/FamilyTreeView.razor](../../src/StarWarsData.Frontend/Components/Shared/FamilyTreeView.razor), structurally a sibling of [AskGraphView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskGraphView.razor):

- Desktop (`d-none d-md-block`) wrapper: `MudPaper` containing a title row, a continuity badge, an advisory chip when `limitations` has any flag, and a `<div id="family-tree-@instanceId" class="f3" style="width:100%;height:720px"></div>` for the chart mount.
- Mobile (`d-block d-md-none`): markdown `MobileSummary` only, identical to [AskGraphView.razor:42-65](../../src/StarWarsData.Frontend/Components/Shared/AskGraphView.razor#L42-L65).
- Loading / error states match `AskGraphView.razor` for visual consistency across Ask descriptors.

JS interop pattern mirrors [GraphViewer.razor:369-479](../../src/StarWarsData.Frontend/Components/Shared/GraphViewer.razor#L369-L479):

- `IJSObjectReference _module` loaded via `JS.InvokeAsync<IJSObjectReference>("import", "./js/family-tree.js")` in `OnAfterRenderAsync` first render.
- `DotNetObjectReference<FamilyTreeView> _self` for callbacks; `[JSInvokable] OnPersonClicked(int pageId)` routes to MudBlazor `NavigationManager` to navigate to the KG node detail page.
- Disposal via `IAsyncDisposable` releases both references.

New JS module at `src/StarWarsData.Frontend/wwwroot/js/family-tree.js`:

- Imports family-chart from the vendored asset path (see *Vendoring* below).
- Exports `renderFamilyTree(containerId, data, dotNetRef)` that calls `f3.createChart`, wires the click handler to invoke the Blazor `OnPersonClicked` callback via `dotNetRef.invokeMethodAsync`, and never calls `.editTree()` (read-only).
- Exports `destroy(containerId)` for clean tear-down on component disposal.

### Vendoring family-chart-premium locally

family-chart-premium is checked in under `src/StarWarsData.Frontend/wwwroot/lib/family-chart-premium/`:

- `family-chart.js` (UMD build from `dist/`; the file keeps its upstream name so a future swap back to MIT family-chart is a one-line path change).
- `styles/` — the `dist/styles/` directory verbatim.
- `LICENSE.txt` — copied verbatim from upstream. The licence requires the notice be retained in all copies, so this file is **not** optional.
- `VERSION.txt` — one line, the upstream commit SHA we vendored from (premium publishes as `0.0.0-beta.x`; the SHA is the only stable identifier until the project leaves beta).

The dependency on `d3@7` is already met — d3 ships with `d3-graph.js`. We do not pull from a CDN at runtime: it adds a third-party request on a logged-in surface, breaks offline dev, and bypasses our build pipeline. This is a new vendoring convention (no prior ADR covers it); if we add a second non-NuGet JS dependency, an ADR codifying the rule is in order.

Per [ADR-004](../../eng/adr/004-mudblazor-deviations.md) the principle of recording library deviations applies — but this is not a *deviation* from family-chart-premium's public API, just a packaging choice. No ADR-004 entry needed.

### Agent prompt routing

[src/StarWarsData.Services/AI/Agents/AskAIAgent.cs](../../src/StarWarsData.Services/AI/Agents/AskAIAgent.cs) gets a routing block above the existing GRAPH VISUALIZATION WORKFLOW:

- `"family tree / lineage / ancestry / kinship / genealogy / parent / child / spouse"` → `render_family_tree` (verify Character type, no other tool needed).
- Everything else that today routes to `render_graph` stays there. The narrow-labels guidance for non-family graphs (just shipped on this branch) is independent of this change and remains.

### Phases

#### Phase 1: Server endpoint + descriptor + tool registration

- `FamilyTreeDescriptor` in `Ask.cs`.
- `BuildFamilyTreeAsync` in `KnowledgeGraphQueryService.cs`.
- `GET /api/RelationshipGraph/family-tree/{pageId}` controller method.
- `render_family_tree` tool in `ChartToolKit.cs` returning the descriptor.
- Unit tests (Skywalker / Solo / Naberrie / Lars) under `StarWarsData.Tests/Unit/`.
- No UI yet. Verifiable via curl / Postman / Aspire MCP `execute_resource_command` on apiservice.

#### Phase 2: Frontend component + JS interop + vendoring

- Vendor family-chart-premium under `wwwroot/lib/family-chart-premium/` (see *Vendoring* above).
- `FamilyTreeView.razor` + `wwwroot/js/family-tree.js`.
- Wire `Ask.razor` to render `FamilyTreeView` when the agent's descriptor is a `FamilyTreeDescriptor` (same dispatch shape as the other descriptors).
- Mobile fallback via `MobileSummary`.
- Chrome DevTools MCP verification on the Skywalker family tree path (desktop + mobile resize).

#### Phase 3: Agent prompt routing + integration tests

- Update `AskAIAgent` system prompt with the new routing block.
- Add an Agent-tier test in `StarWarsData.Tests/Agent/` that confirms the agent picks `render_family_tree` (not `render_graph`) for a family-tree question.
- Live verification via Chrome DevTools MCP on the Ask page against the running AppHost.

## Alternatives considered

- **Extend `render_graph` with `layoutMode=FamilyTree`** — rejected. `RelationshipGraphResult` (nodes + edges with labels) and family-chart's data shape (people + rels) diverge enough that they cannot share a descriptor without one side carrying nullable fields it never uses. Two tools is cleaner than one tool with a hidden type-discriminating mode.
- **Use d3-org-chart or react-family-tree** — rejected. d3-org-chart treats reports-to as the only relation; spouse pairing is not a first-class primitive. react-family-tree pulls React into a Blazor app that has no other React surface, doubling the JS toolchain.
- **Build family layout from scratch in `d3-graph.js`** — rejected. Marriage clustering, generation alignment, ancestor/descendant balancing, and zoom-to-fit on irregular trees are non-trivial. family-chart-premium already solves these and ships the kinship-engine / spouse-link-text plugins we need. Building it ourselves is months of work for a feature with a known good off-the-shelf renderer.
- **MIT [donatso/family-chart](https://github.com/donatso/family-chart) instead of the premium fork** — considered. Same JS API, same data shape, OSI-friendly licence. Rejected because the MIT version lacks the `spouse-link-text` and `kinship` plugins that close two of our Open Questions (partner-vs-spouse rendering and `has_relative` handling). The premium licence's non-commercial grant is satisfied by this project's status, so the trade is worth it. Fallback target if the project ever takes on commercial activity (see *Revisit when*).
- **CDN-hosted family-chart-premium from unpkg** — rejected. Offline dev breaks, third-party request on logged-in surface, no reproducible build, no integrity check.

## Open questions

- ~~**`partner_of` vs `spouses[]` distinction**~~ **Resolved by switching to family-chart-premium.** The `spouse-link-text` plugin labels the inter-spouse line, so we keep the distinction visible: `partner_of` renders as `Partner`, `spouse_of`/`married_to` as `Spouse`. Both still live in `rels.spouses[]` for layout purposes, but the rendered label differentiates them. `limitations.demotedPartners` is no longer emitted.
- ~~**`has_relative` ambiguity** (cousins, aunts, uncles, in-laws)~~ **Resolved by the premium `kinship` plugin.** Rather than excluding `has_relative` and building a separate sidebar, we project these edges into the kinship engine's secondary view. The agent can then route follow-up questions ("who are Leia's in-laws?") through the kinship view without leaving the family tree. `has_relative` entries land in a `kinship[]` block on the response alongside `people[]`.
- **Cycle handling** — Star Wars has clones (Boba Fett ↔ Jango Fett) and one bona-fide time-travel weirdness (Ezra's grandfather). Default: trust family-chart-premium's behaviour; if it crashes or renders an infinite loop, fall back to the legacy `render_graph` Tree mode with a `limitations.cycleFallback=true` flag. Open: is the fallback worth wiring, or do we just document the edge case?
- **Synthetic stubs** — when a referenced parent is missing from the BFS result set (truncated at maxNodes, or genuinely absent in kg.nodes), we emit a stub `Unknown Unknown` placeholder so the renderer doesn't crash. With premium's dynamic card styling we can render these as a distinct "Unknown ancestor" card variant so the user sees that the data, not the renderer, is the gap. Open: should the stub carry the actual PageId so a click navigates somewhere, or stay a true placeholder?

## Revisit when

- A new `adopted_by` / `biological_parent_of` edge label lands in the KG ETL — at that point Phase 1's adoptive-exclusion rule becomes obsolete and the limitations chip should flip from "excluded" to "rendered with adoptive marker".
- **The project takes on any commercial activity** (ads, paid hosting, paid features, paid tiers — Buy Me a Coffee donations don't count). family-chart-premium's non-commercial grant lapses at that point; the project must either buy a commercial licence key or migrate to the MIT [donatso/family-chart](https://github.com/donatso/family-chart) and accept the loss of the `spouse-link-text` / `kinship` plugins (re-open the Open Questions above).
- **family-chart-premium leaves beta** (cuts a non-`0.0.0-beta.x` release) — update `VERSION.txt` from a commit SHA to a semver pin, re-test against the new build, and audit the changelog for any data-shape changes.
- Two or more non-NuGet JS dependencies are vendored under `wwwroot/lib/` — codify the vendoring convention in a new ADR.
- The Character infobox schema gains structured gender (non-binary, etc.) — the default-to-`"M"` mapping becomes a bug rather than a library limitation, and the projection rule must change.
- A second AI agent surface (beyond Ask) wants to render family trees — the descriptor + component pair should move from "Ask-specific" to a shared surface.
