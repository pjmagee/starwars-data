# Design-028: Tri-view node/edge presentation + Holocron wipe

**Status:** Proposal
**Date:** 2026-04-29
**Author:** Patrick Magee + Claude
**Related:**
- [Design-018 — KG enrichments architecture](018-kg-enrichments-architecture.md) (storage contract)
- [Design-019 — UI provenance for Phase 1 vs Phase 2](019-kg-enrichment-ui-provenance.md) (provenance markers — extended here)
- [Design-020 — Holocron async pipeline](020-holocron-async-pipeline.md) (write path)
- [Design-021 — Edge bound provenance](021-edge-bound-provenance.md) (`boundsSource` field)
- [Design-024 — Typed NodeBuilders](024-typed-node-builders.md) (foundation regeneration)
- [Design-025 — Holocron tool-using agent](025-holocron-tool-using-agent.md) (proposal types)
- [Design-026 — Holocron orchestration pattern](026-holocron-orchestration-pattern.md) (Apply path)
- [eng/docs/edge-labels-and-bidirectionality.md](../docs/edge-labels-and-bidirectionality.md) (background research)

## TL;DR

Holocron output is currently low-quality, but its data is guaranteed-isolated from the foundation by storage contract — Phase 1 owns `kg.nodes` / `kg.edges`; Phase 2 only writes `kg.enrichments` / `kg.edge_enrichments` / `kg.events` / `kg.node_processed_chunks`. Today's UI ([Design-019](019-kg-enrichment-ui-provenance.md)) merges them with provenance markers but never lets a user *isolate* either layer, and there's no first-class operation to wipe Holocron output without touching the foundation.

This doc proposes:

> **A three-tab pattern (Original / Holocron / Merged) on every selected-node and selected-edge expansion view, plus a one-shot per-node and global "Wipe Holocron" admin action.** The Merged tab is default; the storage layer already supports the wipe with no foundation risk.

The work is largely UI + a handful of admin endpoints; the read-side data plumbing is covered by the existing `kg.nodes.enriched` / `kg.edges.enriched` views (migration 0010). This is also a *Holocron debugging tool* — the side-by-side Original vs Holocron view is the cleanest diagnostic surface for the agent's current quality issues.

## Problem

Three concrete shortcomings of today's UI:

1. **No "what would the graph look like without Holocron?" view.** Users (and authors auditing AI quality) cannot see the foundation in isolation. Provenance markers help but require visually subtracting the Phase 2 rows by eye.
2. **No "what is Holocron actually contributing?" view.** A reviewer wanting to audit a specific run's output has to scroll the merged view and decode the markers. The Holocron log page exists, but it's an event log, not a per-node/per-edge presentation.
3. **No safe wipe.** Holocron is iterating fast and producing flaky proposals. Every dev wipe today is a manual `db.kg.enrichments.deleteMany({...})` invocation. There's no per-node scope, no admin button, and no audit trail of what was wiped.

The first two are read-side; the third is admin-action. Both rest on the same observation: **the storage layer already enforces the isolation.** What's missing is surfacing it.

## Goals

1. **Layer isolation visible on demand.** Users can switch to a tab that shows pure foundation data, or pure Holocron output, with one click — and back to the merged default with one click.
2. **Wipe-Holocron is a documented, audited admin operation** with both per-node and global scopes. Foundation data is provably untouched.
3. **Same pattern for nodes and edges.** A selected node's detail view and a selected edge's expansion both expose the same three tabs.
4. **Default UX is unchanged.** Merged tab is the landing tab; users who don't care about provenance see today's behaviour.
5. **Zero new ETL.** No new collections, no migrations, no pipeline phase additions. All read-side projection over collections that already exist.

## Non-goals

- Per-list-item provenance. (Same v1 policy as [Design-019](019-kg-enrichment-ui-provenance.md): a property's value list shows union; individual items aren't tagged in v1.)
- Interactive merge resolution. The merge is read-time and deterministic — no user "accept/reject this enrichment" workflow lives in this design (that's the existing Holocron Log page's job).
- Wipe scoped by operation type or status. v1 wipes everything Holocron has produced for the chosen scope. Finer slicing can come later if needed.
- Tabs on the Galaxy Map, Timeline, or d3 Graph Explorer. v1 covers the Knowledge Graph page's selected-node and selected-edge panels only. Other surfaces follow once the visual language is validated.

## The three tabs — definitions

For every node and every edge expansion:

| Tab | Source collection(s) | Read semantics | Default? |
|---|---|---|---|
| **Original** | `kg.nodes` / `kg.edges` | Foundation as built by `InfoboxGraphService`. No enrichment overlay. Reads exactly what Phase 5 produced. | No |
| **Holocron** | `kg.enrichments` / `kg.edge_enrichments` filtered to `status: "Active"` | Phase 2 output only. Each row carries operation (Add / Augment / FillGap / Annotate), evidence, chunk references, run id. | No |
| **Merged** | `kg.nodes.enriched` / `kg.edges.enriched` (existing views from migration 0010) | Foundation overlaid with active enrichments per the merge table below. Same view as today's [Design-019](019-kg-enrichment-ui-provenance.md) markers. | **Yes** |

**Wording.** "Original" is the user's term and gets the tab label. The body copy uses "Foundation (infobox)" once for clarity, then "Original" thereafter. "Holocron" is unambiguous. "Merged" beats "Combined" because it matches the `kg.nodes.enriched` view's mental model.

## Merge semantics — formalised

The merge logic exists today implicitly in `kg.nodes.enriched` / `kg.edges.enriched`. This design pins it down so future tools and users can reason about it.

### For properties (`kg.enrichments`)

| Operation | Foundation field state | Merge result | Provenance tag in Merged tab |
|---|---|---|---|
| `Add` | Field absent | Holocron value present | "Phase 2 only" — full row tinted secondary |
| `Augment` | Field is a list, has values | Union of foundation list + Holocron items, deduped | "Phase 1 + Phase 2" — `+N` chip on row |
| `FillGap` | Field exists but value is null | Holocron value present | "Phase 2 (filled)" — row tinted, badge says "filled" |
| `Annotate` | Field exists with value | Foundation value preserved verbatim | "Phase 1 + Phase 2 context" — secondary dot, hover shows annotation |

`FillGap` v1 policy ([Design-019](019-kg-enrichment-ui-provenance.md) §Non-goals): never overwrites a non-null foundation value. Read-time conflict resolution is therefore a left-join and doesn't need user mediation.

### For edges (`kg.edge_enrichments`)

| Operation | Foundation edge state | Merge result | `boundsSource` ([Design-021](021-edge-bound-provenance.md)) |
|---|---|---|---|
| `Add` | No edge with `(fromId, toId, label)` | New edge, no foundation counterpart | `Holocron` |
| `FillGap` | Edge exists, `fromYear`/`toYear` null or `boundsSource ∈ {Unknown, Lifecycle}` | Foundation edge with refined years | Promoted `Lifecycle → Holocron`; never `Infobox → Holocron` |
| `Annotate` | Edge exists | Foundation edge preserved; Holocron context (qualifier, role, description) shown as expandable detail | Unchanged |
| `Augment` | n/a | **Rejected at `propose_edge` validation time.** Edges are tuples — there's nothing on them to extend. | n/a |

The "never `Infobox → Holocron`" rule is already enforced at write time per [Design-021](021-edge-bound-provenance.md); restating it here so the merged-view contract is complete.

## Server contract

### Read endpoints

Three options, in order of preference:

**Option A — single endpoint with view param (recommended).**

```
GET /api/KnowledgeGraph/node/{pageId}?view=original|holocron|merged
GET /api/KnowledgeGraph/node/{pageId}/edges?view=original|holocron|merged
```

Default `view=merged` if omitted. The same DTO shape (`TemporalNodeDto`) is returned for all three; in `original` mode `EnrichmentMarkers` is empty and rows derived from enrichments are absent; in `holocron` mode `EnrichmentMarkers` is populated and the foundation rows are absent (only Phase 2 rows present); in `merged` mode the Design-019 behaviour is exact.

Pros: one endpoint; clients reuse a single DTO; trivial to swap. Cons: server has to support three projection variants, but they're all on top of the existing `kg.nodes.enriched` / `kg.edges.enriched` pipeline (filter rows by source-flag).

**Option B — three sibling endpoints.** `/foundation`, `/holocron`, `/merged`. Cleaner separation but UI client has to know all three URLs. Worth considering if the projections diverge in shape over time.

**Option C — fetch merged once, project locally on the UI.** Cheapest server-side, but requires the merged DTO to carry per-row `source` tags so the UI can filter. Risk: UI logic and server logic drift; future endpoints (galaxy map, timeline) would need to redo the projection.

**Recommendation:** Option A. Worst case it later splits into Option B without breaking clients (kept as URL aliases).

### DTO additions

Extend `TemporalNodeDto` and the edge DTOs with a per-row source tag:

```csharp
public enum KgRowSource { Foundation, HolocronAdd, HolocronAugment, HolocronFillGap, HolocronAnnotate }

public record PropertyRow(
    string FieldPath,
    object Value,
    KgRowSource Source,
    string? EnrichmentId = null);   // for deep-link to /holocron
```

Edges similarly grow `Source` and `EnrichmentId`. The `KgRowSource` enum is what the UI tab filter uses (`Foundation` only for Original tab, all `Holocron*` for Holocron tab, all values for Merged).

`EnrichmentMarkers` from Design-019 stays — it's the marker subset of the same data, kept for backwards compat with the existing chip-rendering code paths.

### Wipe endpoints

```
POST /api/admin/holocron/wipe                           — global wipe, requires admin
POST /api/admin/holocron/wipe/node/{pageId}            — per-node wipe, requires admin
```

Both behave identically except for the filter:

```csharp
var pageFilter = pageId is null
    ? FilterDefinition<...>.Empty
    : Builders<...>.Filter.Eq("targetPageId", pageId);

await db.GetCollection<NodeEnrichment>("kg.enrichments").DeleteManyAsync(pageFilter);
await db.GetCollection<EdgeEnrichment>("kg.edge_enrichments").DeleteManyAsync(pageFilter);
await db.GetCollection<HolocronEvent>("kg.events").DeleteManyAsync(pageFilter);
await db.GetCollection<NodeProcessedChunks>("kg.node_processed_chunks").DeleteManyAsync(pageFilter);
```

`kg.nodes` and `kg.edges` are **never** touched by these endpoints. The contract is enforced both by code (this is the only delete-Holocron path) and by reviewability (a reviewer can grep the codebase for any `DeleteMany` on those collections — there should be exactly zero hits outside `InfoboxGraphService.RebuildAsync`'s drop-and-rebuild path).

The endpoints write a `kg.events` row of type `HolocronWiped` *before* the deletes (so the audit row survives the wipe — the global wipe deletes its own event row last; the per-node wipe deletes by `targetPageId` filter and the audit row is keyed to a synthetic `0` page so it survives).

### Aspire HTTP commands

Add two HTTP commands on the **admin** resource in [src/StarWarsData.AppHost/Program.cs](../../src/StarWarsData.AppHost/Program.cs):

- `Wipe Holocron (global)` — POST to `/api/admin/holocron/wipe`
- `Wipe Holocron (per-node)` — parameterised on `pageId`

These appear in the Aspire dashboard alongside the existing ETL phase commands.

## UI shape

### Knowledge Graph page — selected-node panel

The panel that today shows a flat properties table + relationship chips becomes:

```razor
<MudTabs Elevation="0" Rounded="true" ApplyEffectsToContainer="true">
    <MudTabPanel Text="Merged" Icon="@Icons.Material.Filled.Merge">
        @* Existing Design-019 rendering with markers — unchanged *@
    </MudTabPanel>
    <MudTabPanel Text="Original" Icon="@Icons.Material.Filled.Source">
        @* Foundation only: filter rows by Source == Foundation *@
        @* Empty-state copy: "This node's foundation comes from the {InfoboxTemplate} infobox." *@
    </MudTabPanel>
    <MudTabPanel Text="Holocron" Icon="@Icons.Material.Filled.AutoAwesome">
        @* Phase 2 only: filter rows by Source.StartsWith("Holocron") *@
        @* Each row shows operation, evidence excerpt, chunk reference, run id *@
        @* Empty-state copy: "No active Holocron enrichments for this node." *@
        @* Header action: "Wipe Holocron for this node" button — confirm modal *@
    </MudTabPanel>
</MudTabs>
```

Default-selected tab is `Merged`. Tab choice is **not** persisted in URL or local storage in v1 — every node click resets to Merged. (Persisting is an easy follow-up if users complain.)

### Edge expansion view

Same three tabs on the row that expands when a user clicks an edge in the relationship table. Tab semantics are identical:

- **Original** — pre-Holocron edge attributes only (`label`, `fromYear`/`toYear` from Phase 1, `evidence`, `meta.qualifier`, etc).
- **Holocron** — only the enrichment attached to this edge (refined bounds, annotation context, role qualifier).
- **Merged** — current behaviour with `boundsSource` chip indicating whether the bounds were Holocron-refined ([Design-021](021-edge-bound-provenance.md)).

### Holocron Log page

A new "Wipe global" button in the page header, behind a confirm modal that requires typing the literal string `wipe holocron` to enable the submit button. Same destructive-action UX pattern that admin Mongo tooling uses elsewhere.

### Visual language

- **Original tab** — chips/rows in default colours, no secondary tints. Looks like the pre-Holocron app.
- **Holocron tab** — every row outlined in `Color.Secondary` (matches Design-019's "Phase 2 only" styling). Each row is essentially a Holocron-Log entry inlined into the node panel.
- **Merged tab** — Design-019 visual language verbatim. Default colours with secondary markers/tints/dots per operation.

## Wipe-Holocron — what it does and doesn't touch

| Collection | Touched by global wipe? | Touched by per-node wipe? | Notes |
|---|---|---|---|
| `kg.nodes` | **No** | **No** | Foundation. Regenerable from `raw.pages` via Phase 5. |
| `kg.edges` | **No** | **No** | Same. |
| `kg.labels` | **No** | **No** | Materialised from `kg.edges`. Regenerable. |
| `kg.enrichments` | Yes (all) | Yes (filtered by `targetPageId`) | Phase 2 property output. |
| `kg.edge_enrichments` | Yes (all) | Yes (filtered by `targetPageId`) | Phase 2 edge output. |
| `kg.events` | Yes (all) | Yes (filtered) | Phase 2 audit log. The wipe records its own event before the delete. |
| `kg.node_processed_chunks` | Yes (all) | Yes (filtered) | Per-node chunk-processing watermark. Wiping forces full re-ingest on next Holocron run. |
| `kg.label_suggestions` (future, [Design-025](025-holocron-tool-using-agent.md)) | Yes (all) | Yes (filtered by `submittedForPageId`) | Future tool surface. |
| `raw.*` | **No** | **No** | Source data. Untouchable by Holocron pathway entirely. |

If a wipe is followed by a `kg.nodes.enriched` / `kg.edges.enriched` read, the merged view collapses to just the foundation — exactly what the Original tab shows. This is the safety property the storage contract gives for free.

## Migration path

### Phase 1 — Read-side projection + tabs (KG page)

1. Add `KgRowSource` enum + `Source`/`EnrichmentId` to `PropertyRow` and edge DTOs.
2. Extend `KnowledgeGraphQueryService` to support `view=original|holocron|merged` filtering on top of the existing `kg.nodes.enriched` / `kg.edges.enriched` pipelines.
3. Wire `MudTabs` on the selected-node panel + edge expansion. Default tab = Merged. Reuse Design-019 marker rendering verbatim in the Merged tab.
4. Verify the Original tab renders identically to the pre-Holocron app on a known node (Anakin would be a good fixture — has both Phase 1 and Phase 2 data).

### Phase 2 — Wipe endpoints + audit

1. Add `POST /api/admin/holocron/wipe` and `POST /api/admin/holocron/wipe/node/{pageId}` to `Admin/Features/Admin/AdminController.cs`.
2. Add the `HolocronWiped` event type to `kg.events` schema (validator update in [validators.js](../../src/StarWarsData.MongoDbMigrations/lib/validators.js)).
3. Add the two Aspire HTTP commands.
4. Wire the per-node "Wipe for this node" button in the Holocron tab header.
5. Wire the global wipe button on the Holocron Log page header with the type-to-confirm modal.

### Phase 3 — Apply pattern to Pages-side detail view

The Pages-side per-page detail view ([src/StarWarsData.Frontend/Components/Pages](../../src/StarWarsData.Frontend/Components/Pages)) renders the same kind of properties + relationships block. Apply the same three-tab pattern. Mostly mechanical once Phase 1 lands.

### Phase 4 — Apply to Graph Explorer / Galaxy Map / Timeline (later)

Punted from Design-019 already; remains punted here. The visual language gets validated on the KG page first.

## Risks

1. **Three round-trips on tab switch.** Default approach is to lazy-fetch on tab change. Mitigation: prefetch all three projections on first node expansion since each is small (a single node's properties + edges fit comfortably in one response). Empty-state tabs remain instant.
2. **Original tab confusion.** Users may interpret "Original" as "raw infobox before any KG processing". Mitigation: tab tooltip/body copy says "Foundation built from the {InfoboxTemplate} infobox" and the empty-state copy reinforces it. Renaming to "Foundation" is an option if user testing shows confusion.
3. **Wipe-Holocron during an in-flight Holocron run.** A wipe issued mid-run could race with the Apply executor's writes. Mitigation: the wipe endpoint takes a per-pageId lock (or globally, takes the same lock the Holocron workflow uses) before deleting. ADR-006's long-running workflow framework already provides per-page locks ([ADR-006](../adr/006-long-running-ai-workflow-pipelines.md)); wiring the wipe through the same lock prevents the race. If a run is active, the wipe waits or returns 409 with a "run X is active for this node" message.
4. **Inconsistent merged view if `kg.nodes.enriched` / `kg.edges.enriched` are out of date.** These are views, not materialised collections, so they're consistent with the underlying base + enrichment collections at every read. No staleness risk.
5. **Wipe surface area.** Currently `kg.events` keeps an audit even after wipe (the wipe writes its own row first). If the wipe deletes events too, it self-erases. Mitigation: keep `HolocronWiped` events out of the per-pageId filter for the wipe — they have a synthetic `targetPageId` of `-1` so the wipe never deletes its own audit. Codified as a magic-number contract; documented in `validators.js` schema comment.
6. **Default-tab regression on URL share.** A user shares a node URL — the recipient lands on Merged, the same as today. No regression. If we later persist tab choice in the URL (`?view=holocron`), shared links open on the chosen tab. Either is fine; v1 doesn't persist.
7. **Holocron tab is empty for most nodes today.** Empty-state copy must read well. Suggested: "No Holocron enrichments for this node yet. Run Holocron from the page detail view to enhance." with a link to the per-node enhance trigger ([Design-020](020-holocron-async-pipeline.md)).

## Verification

Manual scenarios on `starwars-dev`:

1. **Anakin Skywalker (rich Phase 1 + Phase 2)** — open node, default Merged tab matches today's view. Switch to Original: Phase 2 tints disappear, "Chosen One" is missing from Titles, no `+N` chips. Switch to Holocron: panel shows ~5–10 rows, each tagged by operation, with chunk evidence visible. Run "Wipe for this node": Holocron tab empties, Merged collapses to Original.
2. **Yoda (Phase 2-only `Add` edges)** — Original tab is missing the `led` Phase-2-only edge; Holocron tab shows it with full provenance.
3. **Bare node (no Phase 2 enrichments)** — Holocron tab shows empty state. Merged renders identically to Original.
4. **Global wipe** — confirm modal prevents accidental click; after wipe, every node's Holocron tab is empty; `kg.nodes` document count and `kg.edges` document count are byte-identical pre/post (verify via Mongo MCP `count`).
5. **Wipe during run** — start a Holocron run on Anakin, immediately invoke per-node wipe. Expect 409 + clear error message. Cancel the run, retry the wipe, succeeds.

## Decision points

1. **Single endpoint with `view=` param vs three sibling endpoints.** Recommendation: single endpoint. Easier rollback; URL aliases later if needed.
2. **Tab name for Phase 1.** Recommendation: "Original" (the user's own term). "Foundation" is the body-copy fallback if user testing shows confusion.
3. **Default tab on every node click is Merged, not the last-viewed tab.** Recommendation: yes. Persisting is an easy v2 if users complain; the safer default is "show the holistic view".
4. **Wipe scope.** v1 = global + per-node. Per-run / per-operation-type slicing deferred unless a concrete need arises.
5. **Wipe destroys the per-node Holocron audit (`kg.events` rows for that page) by default.** Acceptable — the wipe itself is audited. If long-term audit is required, exempt `HolocronWiped` events from the wipe filter (see Risk 5).
6. **Edge expansion gets the same three tabs.** Recommendation: yes, in v1. Symmetry matters; the per-edge Holocron tab is also where future per-edge "wipe" or "approve" actions would land.
7. **Pages-side detail view follows in Phase 3, not Phase 1.** Recommendation: yes. KG page first to validate the language; Pages-side is mechanical reuse.
8. **No new design needed for [Design-019](019-kg-enrichment-ui-provenance.md) markers.** Recommendation: this design *extends* Design-019 — markers are the Merged tab's visual language, and Design-019's "open question 1" (per-list-item provenance) remains open and orthogonal.

If 1–8 are confirmed, Phases 1–3 of the migration path are unblocked; Phase 4 (Galaxy Map / Timeline / Graph Explorer) waits on UX validation of the KG page rollout.
