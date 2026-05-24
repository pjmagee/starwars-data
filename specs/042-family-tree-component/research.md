# Phase 0 Research — Family Tree Component

**Branch**: `feature/042-family-tree-component` | **Date**: 2026-05-24 | **Plan**: [plan.md](./plan.md)

This file resolves the "NEEDS CLARIFICATION" items and library-choice questions surfaced by the plan, and records the rationale + alternatives so a future maintainer can re-derive the decisions.

---

## R-1: family-chart-premium dist shape and entry point

**Decision**: Vendor `dist/family-chart.js` (UMD) under `wwwroot/lib/family-chart-premium/`. Reference it from `wwwroot/js/family-tree.js` via a plain `<script>` tag added to `App.razor` (alongside the existing `d3.v7.min.js` reference). The runtime global stays `f3` — confirmed by the upstream `package.json` `buildName: "family-chart"` and the parent MIT project's published API.

**Source**: **npm tarball, not the GitHub repo.** Discovered during Phase 4: the upstream `donatso/family-chart-premium` repo on GitHub publishes only `README.md`, `LICENSE.txt`, and `package.json` — there is no `dist/` directory and no `src/`. The actual code ships exclusively via npm under `family-chart-premium@beta` (e.g. `0.0.0-beta.2`). Use `npm pack family-chart-premium@beta` rather than `gh api`. The `VERSION.txt` records both the npm tarball shasum (the authoritative identifier of the actual code) and the matching upstream main-branch commit ref (for cross-reference even though the repo doesn't carry the dist). Quickstart §1 has the canonical recipe.

**Rationale**:

- `package.json` declares only `"main": "dist/family-chart.js"` and `"unpkg": "dist/family-chart.min.js"` — no ESM exports field is published. UMD is the only deliverable.
- The repo's existing Blazor JS interop pattern (`GraphViewer.razor` → `wwwroot/js/graph-viewer.js`) imports via `JS.InvokeAsync<IJSObjectReference>("import", "./js/graph-viewer.js")` — an ES module that itself references globals registered by the UMD `<script>` tags in `App.razor`. We mirror that: `family-tree.js` is an ES module that references `window.f3` provided by the UMD bundle loaded at app boot.
- The upstream `buildName: "family-chart"` is intentional — the premium fork keeps the global name so swapping back to MIT family-chart on a commercial pivot is a one-line `<script src=…>` change.

**Alternatives considered**:

- **Dynamic `import()` of the UMD via a blob URL** — over-engineered. UMD is meant to be `<script>`-tag-loaded; the blob detour adds no isolation since the bundle still pollutes `window.f3`.
- **Bundle through a small esbuild step** — adds a JS build pipeline to a repo that has none. Defer until / unless a second non-NuGet JS dep arrives (already flagged in spec § Revisit when).
- **`gh api repos/.../contents/dist/...`** — assumed by the original plan, but the dist isn't tracked in the repo. Would have to be reconstructed from npm anyway. Use `npm pack` from the start.

---

## R-2: Premium plugins (`spouse-link-text`, `kinship`) — packaged or separate imports?

**Decision**: Treat both as plugins that must be enabled at chart-construction time, not as features automatically active in the base bundle. The canonical API (verified in Phase 4 against `dist/types/index.d.ts` in `family-chart-premium@0.0.0-beta.2`) is:

```js
chart.use(new window.f3.SpouseLinkTextPlugin({ text: (sp1, sp2) => 'Spouse' }));
const k = new window.f3.KinshipPlugin();
k.setSelfId(rootId);
chart.use(k);
```

`SpouseLinkTextPlugin` and `KinshipPlugin` are exported classes on the `f3` global. `chart.use(pluginInstance)` is the registration entry point. The README's `f3.plugins.spouseLinkText({…})` shorthand is **not** on the public surface — the d.ts declarations expose the classes directly.

**Rationale**:

- The upstream README lists kinship-engine and spouse-link-text under "🧩 Power of Plugins" with separate demo paths under `src/plugins/<plugin>/__demos__/`. Plugin = opt-in, even when bundled.
- The MIT family-chart codebase exposes a `.plugin()` chain on the chart object; the premium fork is structurally a fork of that codebase. Pattern carries over.

**Verification probe (Phase 2)**: Before wiring the JS module, download the dist, run `Get-Content dist/family-chart.js | Select-String -Pattern 'plugin'` to confirm the exact entry-point names. If they differ from the README references, update `family-tree.js` and the contracts/render-family-tree-tool.md accordingly. Do NOT silently strip the plugins — that's the entire justification for using premium over MIT.

**Alternatives considered**:

- **Assume plugins are auto-applied** — risky. If `spouse-link-text` requires opt-in we'd silently ship the bug we said we resolved (partner-vs-spouse loss).

---

## R-3: Watermark / license-key behaviour in the free tier

**Decision**: Document upstream restriction #2 (do not circumvent watermark / license-key validation) in `wwwroot/lib/family-chart-premium/LICENSE.txt`, then **accept any watermark the free build produces**. Do not patch the bundle. If a watermark surfaces and the user finds it objectionable, the path forward is either (a) purchase a commercial key (not currently available — `donatso.dev@gmail.com` per README), or (b) migrate to MIT [donatso/family-chart](https://github.com/donatso/family-chart) and re-open the relevant Open Questions in spec.md.

**Rationale**:

- The licence is binding even on free use. Restriction #2 is unambiguous: "You may not circumvent, disable, or remove the license key validation or watermark features."
- The upstream README has no screenshot or text describing what the free-tier watermark looks like. Behaviour is unknown until we ship the vendored bundle.

**Verification probe (Phase 2)**: When the Skywalker family tree first renders in the Chrome DevTools MCP verification, snapshot the chart container and visually inspect for a watermark, branded badge, or "Powered by …" footer. Record findings in the screenshots directory.

**Alternatives considered**:

- **Pre-validate the dist by reading the source** — the premium repo intentionally publishes only the README + LICENSE + package.json publicly; the actual dist build presumably ships from a private CI. We learn the watermark behaviour by running it, not by reading code we don't have.

---

## R-4: Pinning a beta version

**Decision**: Pin to a specific upstream commit SHA in `wwwroot/lib/family-chart-premium/VERSION.txt` (one line, full 40-char SHA). When Phase 2 begins, capture the current SHA of the `main` branch's last commit that touched `dist/`. Track upstream via a manual periodic check, not auto-update.

**Rationale**:

- Upstream is `0.0.0-beta.2`. Semver-pinning is meaningless until they cut a stable release.
- Commit SHA is the only stable identifier. A diff between two SHAs is trivially reproducible; a diff between two `0.0.0-beta.2` snapshots is not.
- Manual revisit cadence beats auto-update for a beta dependency on a non-OSI licence — surprise behaviour changes from upstream are higher cost than slightly-stale local copies.

**Alternatives considered**:

- **`unpkg` with version pinning** — rejected for the same reasons as the MIT version (offline dev breaks, third-party request on a logged-in surface, no integrity check).
- **Git submodule of the premium repo** — overkill for a single UMD file. Also pulls in the whole repo (assets, demos) when we want one bundle.

---

## R-5: `Gender` field source-of-truth (KG-First Principle VI gap)

**Decision (final)**: Read `Gender` from `kg.nodes.properties["Gender"]`. Map `"Male"` → `"M"`, `"Female"` → `"F"`, everything else (`"Non-binary"`, missing key, ambiguous value) → `"M"` + record the PageId in `limitations.missingGenders` on the response. The `"M"` default is a family-chart constraint (it requires `"M"|"F"`), not a model decision, and MUST NOT leak anywhere else in the codebase. **Principle VI fully honoured — no `raw.pages` read at runtime.**

> **Correction (2026-05-24, post-implementation).** This entry originally claimed `kg.nodes` did not surface a `gender` field and proposed reading from `raw.pages.infobox.Data` as a "documented Principle-VI soft-edge". That premise was wrong: `"Gender"` is listed in [`FieldSemantics.cs:29`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs#L29) (`FieldSemantics.Properties`), so the generic `NodeBuilderBase` loop projects it onto `kg.nodes.properties["Gender"]` for every Character (and every other template type whose [`TemplateFields.g.cs`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/TemplateFields.g.cs) lists "Gender" — Deity, Yuuzhan-Vong types, etc.). The original kg-expert implementation read from `raw.pages` and was a real Principle-VI violation; the refactor in commit `<this commit>` switches to `node.Properties["Gender"]` and drops the bulk raw.pages fetch from `BuildFamilyTreeAsync`. There is **no soft-edge** here, and no ETL follow-up required.

**Rationale (revised)**:

- `kg.nodes.properties["Gender"]` is populated by the standard NodeBuilderBase loop because the field appears in `FieldSemantics.Properties` and the relevant `TemplateFields.g.cs` template whitelists.
- The kg.nodes BFS pass already loads every visited node — Gender is just one more key on the same document. Zero extra round-trips.
- Soft-handling missing/non-binary values to `"M"` + `Limitations.MissingGenders` is still the right shape — but the cause is the library constraint (renderer requires `"M"|"F"`), NOT a data-availability gap.

**Alternatives considered**:

- **Default everyone to `"M"` with no property lookup** — rejected. Loses the actual gender data we have; defeats family-chart's gender-based card styling.
- ~~**Read from `kg.nodes.properties["Gender"]` if the field happens to be there**~~ — this is now the **chosen** path.
- **Project a top-level `gender` field onto `GraphNode`** — overkill. `properties["Gender"]` is consistent with how every other scalar field is surfaced; a top-level field would be a one-off privilege.

---

## R-6: Cycle handling (Boba Fett ↔ Jango Fett clones; Ezra's grandfather time-travel)

**Decision**: Trust family-chart-premium's behaviour for v1. If the renderer crashes or visibly loops, fall back to the legacy `render_graph` Tree mode with `limitations.cycleFallback=true`. Probe in Phase 2 against Boba/Jango Fett specifically.

**Rationale**:

- The premium fork has the `kinship` engine plugin, which (per the README's relation-aware secondary view feature) likely deals with kinship cycles more gracefully than the bare layout in MIT family-chart.
- Building a server-side cycle detector before knowing whether the renderer needs it is premature.
- The fallback is cheap: `render_graph` Tree mode already exists; the descriptor switch is one branch in the Ask page.

**Verification probe (Phase 2)**: Prompt "Show me Boba Fett's family tree" and verify the tree renders without infinite loop or visual breakage. Document the result in the verification screenshots.

**Alternatives considered**:

- **Reject cycle-containing inputs at the server** — would block Boba Fett (a high-value query) for an issue that may not exist in the renderer. Defer until evidence.

---

## R-7: Synthetic-stub navigability

**Decision**: Carry the *actual PageId* on the synthetic stub (e.g. `{ id: "452391-stub", data: { pageId: 452391, "first name": "Unknown", "last name": "Unknown" }}` and wire the click handler to navigate to the real KG node detail page (`/knowledge-graph/nodes/{pageId}`). This way truncation doesn't leave the user stranded — the stub is a "you've hit the BFS edge; click to explore" affordance, not a dead-end placeholder.

**Rationale**:

- The KG node detail page exists and is the natural "expand from here" surface. Sending the user there from a stub keeps the tree useful at truncation boundaries.
- Stubs that are *also* genuinely absent from `kg.nodes` (no PageId at all) stay as true placeholders with no click target.
- Family-chart's card click handler is a per-card function — we already wire it to `dotNetRef.invokeMethodAsync('OnPersonClicked', pageId)`. Stubs that have a PageId just join the same flow.

**Alternatives considered**:

- **Stay a true placeholder** — simpler but worse UX. A user clicking on the dead "Unknown Unknown" card has no idea they could have learned more.

---

## R-8: BFS bound — `maxDepth` interpretation

**Decision**: `MaxDepth` is interpreted symmetrically — N generations of ancestors AND N generations of descendants from the root, plus the root's spouses. Clamp to `[1, 5]` server-side. Default `3`.

**Rationale**:

- The premium `tree-filtering` / `layout-options` plugins let the user widen the depth in either direction at the renderer; but the server projection still needs a hard cap on the BFS so we don't paginate 200K relations on a celebrity character.
- 5 generations covers almost every Star Wars family question a user would phrase ("Skywalker family tree" = 4 generations, "Ren Skywalker's full ancestry" = 5+ but that's a research query, not the common case).
- Spouses of intermediate nodes (Padmé as Anakin's spouse) come for free via the spouse pass — they don't count against the depth budget.

**Alternatives considered**:

- **Separate `ancestorDepth` and `descendantDepth`** — true asymmetric control is a v2 enhancement. v1 keeps the API simple; the renderer-side tree-filtering covers the post-fetch refinement.

---

## R-9: Reusable grounding pattern — sidebar vs Ask

**Decision**: `render_family_tree` lives only in the Ask agent's toolkit (`ChartToolKit.cs`). The SP-4 sidebar does NOT register the tool. Sidebar suggests `/ask` for kinship questions (matches Design-022 § Toolkit explicit exclusion of `render_*`).

**Rationale**:

- The chart needs the larger Ask surface to render meaningfully (~720 px tall, 800 px wide). The 420 px sidebar would render a postage-stamp tree.
- Design-022 already established that the sidebar is text-first. Adding a render-shaped tool there is a Principle-V drift.
- The sidebar's CopilotAgent system prompt has an "if a question wants the bigger surface, suggest `/ask`" escape hatch already — `family tree` phrasing slots into that escape hatch naturally.

**Alternatives considered**:

- **Mini-tree in the sidebar via the premium `mini-tree-with-full` plugin** — interesting but premature. Wait for evidence that users want kinship answers in the sidebar before adding the surface.

---

## R-10: Returning the tree directly vs through `RelationshipGraphResult`

**Decision**: The endpoint returns the family-chart-shaped JSON directly (`{ rootId, rootName, people[], kinship[], limitations }`), NOT the generic `RelationshipGraphResult { nodes[], edges[] }`. The descriptor mirrors the same shape on the AI side.

**Rationale**:

- The family-chart data shape and `RelationshipGraphResult` diverge enough that one or the other always carries nullable fields it never uses (already noted in spec § Alternatives considered).
- The premium `kinship` plugin needs a `kinship[]` block alongside `people[]` for `has_relative` entries; that's a shape the generic result type doesn't model.
- Two endpoints + two descriptors is cleaner than one with a type discriminator.

**Alternatives considered**:

- **Return `RelationshipGraphResult` and project on the client** — would push family-chart-shape logic into Razor, away from the existing server-side projection patterns. The unit tests would all have to move to the frontend too.

---

## NEEDS CLARIFICATION — resolved

The plan's Technical Context had no items marked `NEEDS CLARIFICATION`; all unknowns were either resolved here or deferred as Phase-2 verification probes (R-2, R-3, R-6). Phase 1 can begin.
