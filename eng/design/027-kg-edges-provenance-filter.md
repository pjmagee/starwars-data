# Design-027: Knowledge-Graph "Edges" tab — provenance filter (Original vs Holocron)

**Status:** Proposal
**Date:** 2026-04-29
**Author:** Patrick Magee + Claude
**Related:** [Design-018 KG enrichments architecture](./018-kg-enrichments-architecture.md), [Design-019 KG enrichment UI provenance](./019-kg-enrichment-ui-provenance.md), [Design-025 Holocron tool-using agent](./025-holocron-tool-using-agent.md), [Design-026 Holocron orchestration pattern](./026-holocron-orchestration-pattern.md)

## Problem

The `/knowledge-graph` page's **Edges** tab ([Frontend/Components/Pages/KnowledgeGraph.razor:607](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraph.razor#L607)) shows an aggregated label-frequency table — one row per canonical label, with count, continuity split, top from/to types, average confidence, and a sample edge. The data source is `kg.edges` (Phase 1 wiki-extracted edges).

**The blind spot:** the table cannot distinguish between labels established by Phase 1 (the wiki's own infobox-derived vocabulary) and labels added by Holocron (Phase 2 enrichments in `kg.edge_enrichments`, including any newly-promoted labels from `kg.label_suggestions` once Design-025's vocabulary-growth tool starts operating).

Today this barely matters because Holocron only adds edges using existing canonical labels. After v2.0 ships:

1. **`suggest_new_label` will surface vocabulary the wiki doesn't have.** Promoted suggestions become real edge labels appearing in `kg.edge_enrichments` with `operation = "Add"`. We need a way to see, at a glance, "show me only the labels Holocron introduced" — both for QA (does this look like signal or like slop?) and for vocabulary maintenance (is the synonym table catching what it should?).
2. **Holocron's edge volume will grow.** Each enhanced node produces 5–50 new staged edges. Without provenance filtering, the label-frequency table mixes "labels with 100k+ Phase 1 edges" alongside "labels with 23 Holocron-only edges," and the latter is invisible at typical sort orders.
3. **Annotate / FillGap doesn't add edges, but it still alters how the label is "used."** A user investigating `apprentice_of` should be able to see "of these 47k edges, X are Phase 1, Y are Phase 1 + Holocron annotations, Z are Holocron-added." The current single count hides the breakdown.

The fix is small but only useful in the v2.0 world. Ship it alongside Design-025/026, not before.

## Requirement

Add a **provenance filter** to the Edges tab with three values:

| Filter value | Shows |
|---|---|
| `All` (default) | Today's behaviour — labels aggregated across `kg.edges` ∪ `kg.edge_enrichments` (operation = Add) |
| `Original` | Labels with at least one edge in `kg.edges` (Phase 1 wiki-extracted) |
| `Holocron` | Labels with at least one Holocron-added edge OR Holocron-introduced label, irrespective of whether Phase 1 also has the label |

Plus a stricter sub-filter (toggle) inside `Holocron`: **"Holocron-only labels"** — labels that exist exclusively in `kg.edge_enrichments` / `kg.label_suggestions` and have **zero** edges in `kg.edges`. This is the highest-signal view for QA: every row is a vocabulary expansion the agent introduced.

### Per-label breakdown columns

When the provenance filter is `All` (the default), each row's `Count` column gets a small breakdown chip set:

```
total: 47,231   ·  P1: 47,180  ·  H+: 51
```

- **P1** — count of edges in `kg.edges` with this label.
- **H+** — count of edges added by Holocron via `kg.edge_enrichments` (operation = `Add`).
- **H~** (only when > 0) — count of Phase 1 edges *annotated* by Holocron via `kg.edge_enrichments` (operation = `Annotate` / `FillGap`).

When the provenance filter is `Original` or `Holocron`, the breakdown chips are pre-filtered to the relevant tier.

## Data shape — what already exists vs. what's needed

### Already present (no schema change)

`kg.edge_enrichments` ([Design-018](./018-kg-enrichments-architecture.md)) carries:

- `fromId`, `toId`, `label` — the edge tuple.
- `operation` — `Add` / `Annotate` / `FillGap`.
- `agentVersion` — `holocron-v1.3.0` etc.
- `status` — `Active` / `Stale` / `Quarantined`.
- `evidence` — chunk + page citations.

The data needed for the filter is in this collection. No schema migration is required.

### What needs to change

**Server-side aggregation: `LoadLabelStats`** at [Frontend/Components/Pages/KnowledgeGraph.razor:639](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraph.razor#L639) calls an API endpoint (likely `/api/knowledge-graph/edge-labels` or similar). That endpoint today aggregates over `kg.edges` only. It needs to additionally:

1. Aggregate `kg.edge_enrichments` (filtered by `status = Active`) grouped by `label`.
2. Bucket counts by `operation` (`Add`, `Annotate`, `FillGap`).
3. Outer-merge with the `kg.edges` aggregation on `label`. Labels appearing only in `kg.edge_enrichments` (no Phase 1 edges) are emitted with `P1: 0`.
4. Apply the provenance filter as a post-aggregation predicate.

**DTO additions:** `EdgeLabelStatsDto` ([wherever it's defined in Models/](../../src/StarWarsData.Models/)) gains:

```csharp
public sealed class EdgeLabelStatsDto
{
    // existing fields...
    public long Phase1Count { get; init; }       // P1
    public long HolocronAddCount { get; init; }  // H+
    public long HolocronAnnotateCount { get; init; }  // H~ (Annotate + FillGap combined)
    public bool HolocronOnly { get; init; }      // true iff Phase1Count == 0
    public string? IntroducedByAgentVersion { get; init; }  // earliest agentVersion that emitted this label, if HolocronOnly
}
```

**Frontend filter UI:** add a `MudSelect` to the existing filter card on the Edges tab, alongside the "Min count" field:

```razor
<MudItem xs="12" sm="6" md="4" lg="3">
    <MudSelect T="EdgeProvenanceFilter" @bind-Value="_provenanceFilter"
               @bind-Value:after="OnLabelFilterChanged"
               Label="Provenance" Variant="Variant.Outlined" Margin="Margin.Dense">
        <MudSelectItem Value="EdgeProvenanceFilter.All">All</MudSelectItem>
        <MudSelectItem Value="EdgeProvenanceFilter.Original">Phase 1 (wiki)</MudSelectItem>
        <MudSelectItem Value="EdgeProvenanceFilter.Holocron">Holocron</MudSelectItem>
        <MudSelectItem Value="EdgeProvenanceFilter.HolocronOnly">Holocron-only labels</MudSelectItem>
    </MudSelect>
</MudItem>
```

**Row template:** the existing `Count` cell renders the breakdown chips inline, using `Color.Primary` for P1, `Color.Tertiary` for H+, and a subdued outlined chip for H~ — matches the per-edge "HolocronAnnotated" colour convention already used inside the per-node Edges table at [KnowledgeGraph.razor:459-468](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraph.razor#L459-L468).

## Cross-link to the per-node Edges table

The per-node expanded **Edges** table (inside a node row, [KnowledgeGraph.razor:488](../../src/StarWarsData.Frontend/Components/Pages/KnowledgeGraph.razor#L488)) **already** distinguishes Phase 1 vs Holocron edges per-edge — the comment at line 459 lists the three states (`Original`, `HolocronOnly`, `HolocronAnnotated`) with their colour conventions. Design-027 just lifts that same provenance distinction one level up — into the *labels* aggregation table — so users can browse "what labels exist by provenance" before drilling into individual edges.

The colour conventions stay consistent across both views:

| Provenance | Per-edge view (existing) | Labels-tab breakdown chip |
|---|---|---|
| Phase 1 only | unchipped / Default | `P1` chip · `Color.Primary` |
| Holocron-added (operation = Add) | Secondary chip | `H+` chip · `Color.Tertiary` |
| Phase 1 + Holocron annotation | Primary + secondary dot | `H~` chip · subtle outlined |

## Implementation outline

### Phase 1 — Backend aggregation update (~2 hours)

- Modify the edge-labels endpoint to compose `kg.edges` aggregation `$unionWith` `kg.edge_enrichments` (Active status), or do two parallel aggregations and merge in C#.
- Note from [project_mongo_view_pagination.md](../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/project_mongo_view_pagination.md) memory: avoid `$lookup + $expr` outer-doc-var patterns for paginated views. Two parallel aggregations + C# merge is the safe shape.
- Bucket Holocron counts by `operation` so the H+ vs H~ split is server-computed.
- Emit `IntroducedByAgentVersion` only for `HolocronOnly` labels — read the earliest `agentVersion` from the `kg.edge_enrichments` Add-records for that label.

### Phase 2 — DTO + API contract update (~30 min)

- Add the four new fields to `EdgeLabelStatsDto`.
- Add a `provenance` query-param to the endpoint accepting `all` / `original` / `holocron` / `holocronOnly`.
- Backwards-compatible — old clients omit the param and get `all`.

### Phase 3 — Frontend filter + breakdown chips (~1 hour)

- Add the `MudSelect` to the filter card.
- Add an `EdgeProvenanceFilter` enum to the Frontend model layer.
- Update the row template to render breakdown chips next to `Count`.
- Wire `OnLabelFilterChanged` to include the provenance value when calling the endpoint.

### Phase 4 — Testing (~30 min)

- Unit: aggregation correctness on a fixture with Phase 1 + Holocron edges sharing a label, plus a Holocron-only label.
- Integration: the endpoint returns expected counts for each provenance filter value.
- Manual: visual check on dev DB after running Holocron on a few nodes.

**Total:** ~4 hours when Design-025/026 v2.0 lands. The work is small, but only worth doing once `kg.edge_enrichments` has Holocron-introduced labels worth filtering — i.e. after v2.0's `suggest_new_label` machinery starts producing real signal.

## Why this is its own design doc, not a section in 025 or 026

Designs 025 and 026 are about the agent / orchestration architecture. This is a Frontend data-presentation concern that depends on the agent shipping its `suggest_new_label` machinery, but is otherwise independent — different code paths, different test surface, different ownership.

Keeping it separate also means it can ship **after** v2.0 has been running for a few weeks, once we've seen what kinds of labels Holocron actually introduces. The design is small enough that landing it as a follow-up doesn't slow the v2.0 cutover.

## Decision points

1. **Provenance filter as a server-side aggregation, not a client-side filter.** Client-side would be simpler but breaks on large label sets — there are 80+ canonical labels and Holocron can grow this further; pagination on the server is the only sustainable shape.
2. **`H~` (Annotate + FillGap) combined into one count.** They're both "Phase 1 edge with Holocron context attached" from the user's perspective; splitting them adds visual noise. If we later need to distinguish, it's a one-line DTO addition.
3. **Ship after v2.0 cutover, not alongside.** The filter is only meaningful once `kg.edge_enrichments` carries Holocron-introduced labels. Premature shipping = empty filter values, no signal.
4. **Reuse the per-edge colour convention from the per-node table.** Don't invent a new palette; the user's eye already knows what Primary / Tertiary mean for provenance in this app.

If aligned on those four, this is a 4-hour follow-up to slot in once v2.0 has produced a couple of weeks of clean Holocron label suggestions.
