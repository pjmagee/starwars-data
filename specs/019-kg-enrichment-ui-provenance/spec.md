# 019 — UI provenance for Phase 1 vs Phase 2 KG data

Status: Partially shipped — Stage E1 shipped 2026-04-26 (`013574df09`): `TemporalNodeDto.EnrichmentMarkers`, `EntityLabelsResult.HolocronOnlyLabels`/`HolocronAnnotatedLabels`, and the Phase 1 vs Phase 2 chip/row styling on `/knowledge-graph`. Stage E2+ (Graph Explorer d3 viewer, Galaxy Map, Timeline) still pending.
Date: 2026-04-26
Author: Patrick Magee
Cross-refs: [Design-018 — KG enrichments architecture](../018-kg-enrichments-architecture/spec.md)

## Problem

Design-018 introduced enrichment collections (`kg.enrichments`, `kg.edge_enrichments`) and merged-read views (`kg.nodes.enriched`, `kg.edges.enriched`). The architecture is in place and the Holocron agent is producing valid Annotate / FillGap / Add proposals. But every user-facing surface still reads from the base collections (`kg.nodes`, `kg.edges`), so Phase 2 enrichments are invisible in the UI.

Worse: even if a consumer migrates to the enriched view, today's UI components don't differentiate Phase 1 facts from Phase 2 enrichments. A user looking at Anakin's "Titles" property cannot tell that "Chosen One" was added by the Holocron agent rather than extracted from the Wookieepedia infobox. That trust signal is the whole point of the architecture — without it, users either over-trust the AI output (treating it as canonical) or distrust the entire dataset.

## Goals

1. **Foundation/enhanced split is visually obvious.** A user landing on any node or edge view should be able to tell at a glance which data came from Phase 1 (infobox extraction) and which came from Phase 2 (Holocron agent).
2. **Reusable provenance pattern.** Establish one rendering convention that every Stage E consumer (Knowledge Graph table, Graph Explorer d3 view, Galaxy Map, Timeline) reuses, so the same visual language applies across the app.
3. **Zero-cost when no enrichments exist.** Nodes/edges without any active enrichment render exactly as they do today — same chip colours, same layout, no extra UI noise.
4. **No new server round-trips.** The enrichment markers ride along with existing API responses; the UI doesn't need a second fetch to know "is this Phase 2?".

## Non-goals

- Surfacing the *full* enrichment detail (claim, evidence, reasoning) inline. That belongs to the existing `/holocron` log page; clicking a Phase 2 marker can deep-link there in a later iteration.
- Per-field provenance for legacy `RelationshipGraphBuilderService` LLM-batch edges. Those edges already have a `sourcePageId`; their provenance story is separate and pre-dates Holocron.
- Read-side merging logic for FillGap conflicts. The v1 policy forbids overwriting non-null values, so merging is a left-join: the base value always wins, and FillGap only ever populates a previously null field.

## The provenance shape

Three buckets cover every Phase 2 effect on a property or label:

| Bucket | What it means | Suggested chip colour |
| --- | --- | --- |
| **Phase 1** | Value/edge came from Wookieepedia infobox extraction. No active Holocron enrichment touches this slot. | `Color.Info` (existing chip default) for relationships; default table styling for properties. |
| **Phase 2 only** | This property has *no* infobox value at all, or this edge label has *no* base edges — the only reason it appears is a Holocron `Add` enrichment. | `Color.Secondary` (MudBlazor purple). The whole row/chip switches colour. |
| **Phase 1 + Phase 2 (annotated/augmented)** | Phase 1 has the value or edge, and Holocron added context: an `Augment` appending list items, or an `Annotate` decorating an edge with role/qualifier/description, or a `FillGap` filling a previously null temporal bound. | Keep `Color.Info`/default. Add a small `Color.Secondary` dot or a "+N" badge on the trailing edge of the chip/row to signal "Phase 1 with Phase 2 overlay". |

Annotate context (`role`, `qualifier`, `description`) is rendered in tooltips/hover-cards rather than inline — keeps the primary view dense, and makes the marker the discoverability hook.

## Server contract

### `kg.nodes.enriched` projection — `TemporalNodeDto.EnrichmentMarkers`

The `BrowseTemporalNodesAsync` response gains a per-property marker list:

```csharp
public class TemporalNodeDto {
    // ...existing fields unchanged...
    public List<EnrichmentMarkerDto> EnrichmentMarkers { get; set; } = [];
}

public record EnrichmentMarkerDto(string FieldPath, string Operation);
```

The list is empty when no active hash-matched enrichment exists for the node. When present, the UI uses `(FieldPath, Operation)` to tag the corresponding row in the attributes table:
- `Operation == "Add"` → render that row in `Color.Secondary` styling (Phase 2 only).
- `Operation == "Augment"` → render the row default but add a "+1" secondary chip after the values (Phase 1 + Phase 2 overlay). The actual added items aren't differentiated within the value list in v1 — that's a refinement that can come later.

### `EntityLabelsResult` — split labels by provenance

The `/api/RelationshipGraph/labels/{nodeId}` endpoint gains two new fields:

```csharp
public class EntityLabelsResult {
    // ...existing fields unchanged...
    public List<string> HolocronOnlyLabels { get; init; } = [];
    public List<string> HolocronAnnotatedLabels { get; init; } = [];
}
```

- `HolocronOnlyLabels` — labels in `Labels[]` where every edge with that label between this node and any neighbour came from a Holocron `Add` enrichment (no base `kg.edges` entry).
- `HolocronAnnotatedLabels` — labels in `Labels[]` where at least one edge has a Holocron `Annotate` or `FillGap` enrichment attached.

A label can appear in `Labels` only, in `Labels` + `HolocronOnlyLabels`, or in `Labels` + `HolocronAnnotatedLabels`. (A label could in principle be both Holocron-only AND have an annotation, but that would mean Phase 2 added the edge AND annotated it — the agent's pre-flight forbids annotating its own Add output, so this case shouldn't arise in v1.)

### Read-side data flow

```
kg.nodes  ─────┐
               ├──$lookup──>  kg.nodes.enriched (view)  ──>  KnowledgeGraphQueryService  ──>  TemporalNodeDto + markers
kg.enrichments ┘                                              (Stage E1 — this PR)

kg.edges  ────┐
              ├──$lookup──>  kg.edges.enriched (view)  ──>  per-label aggregation  ──>  EntityLabelsResult split
kg.edge_enrichments ┘                                       (Stage E1 — this PR)
```

The `$lookup` already exists (migration 0010). The Stage E1 work is purely on the C# read side: change collection bindings, add a small projection step, ship new DTO fields.

## Frontend rendering rules — Knowledge Graph node view

This is the canonical pattern; later Stage E migrations apply the same rules to their own components.

### Attributes table (the table the user screenshotted)

```razor
@foreach (var kvp in context.Properties.OrderBy(p => p.Key))
{
    var marker = context.EnrichmentMarkers.FirstOrDefault(m => m.FieldPath == kvp.Key);
    var rowStyle = marker?.Operation == "Add" ? "background-color: var(--mud-palette-secondary-hover);" : null;
    <tr style="@rowStyle">
        <td>@FormatPropertyKey(kvp.Key)</td>
        <td>
            @string.Join(", ", kvp.Value)
            @if (marker?.Operation == "Augment")
            {
                <MudTooltip Text="One or more values added by the Holocron agent">
                    <MudChip T="string" Size="Size.Small" Color="Color.Secondary" Variant="Variant.Filled" Class="ml-2">+1</MudChip>
                </MudTooltip>
            }
        </td>
    </tr>
}
```

Add (whole new property) → secondary background tint on the row.
Augment (extra values on an existing list) → row stays default, trailing secondary chip.

### Relationship chips

```razor
@foreach (var label in _expandedLabels)
{
    var phase2Only = _holocronOnlyLabels.Contains(label);
    var annotated = _holocronAnnotatedLabels.Contains(label);
    var color = phase2Only ? Color.Secondary : Color.Info;

    <MudChip T="string" Size="Size.Small" Color="@color" Variant="Variant.Outlined">
        @FormatPropertyKey(label)
        @if (annotated)
        {
            <MudTooltip Text="One or more edges with this label have Holocron context attached">
                <span style="display:inline-block; width:6px; height:6px; border-radius:50%;
                             background-color: var(--mud-palette-secondary); margin-left:4px;"></span>
            </MudTooltip>
        }
    </MudChip>
}
```

Phase 2-only labels switch to `Color.Secondary`. Phase 1 labels with Phase 2 annotations keep their colour but get a secondary dot.

## Out of scope for this PR

- d3 graph in `/graph-explorer` — same provenance split applies, but d3 needs different rendering primitives (edge stroke colour, hover tooltips with role/qualifier text). Punted to a follow-up Stage E2.
- Galaxy Map and Timeline — Stage E3+, after we've validated the visual language on the Knowledge Graph page.
- Dropping the admin gate on `POST /api/holocron/enhance/{pageId}` so users can refresh nodes themselves. Independently scoped: a small auth-only change that doesn't touch the enriched-view plumbing. Tracked in the handoff memory; will land in its own commit before or alongside this work.

## Risks

1. **Aggregation cost on the labels endpoint.** Today the labels endpoint runs a single `Distinct` against `kg.edges` filtered by `fromId | toId`. The Phase-2 split needs an additional aggregation against `kg.edge_enrichments` (or a single aggregation pipeline against `kg.edges.enriched`). For high-degree nodes this could be slower. Mitigation: cap the per-call enrichment lookup at the same node-degree limit the existing query already applies, and ensure `kg.edge_enrichments` has indexes on `(fromId, status)` and `(toId, status)`.
2. **Visual noise from many enrichments.** A node with 20 Holocron-touched properties would render 20 secondary-tinted rows, defeating the "scan and spot the agent's work" goal. v1 acceptable: the agent runs at most ~10 enhancements per node-pass, and only on a small fraction of the corpus. If/when this becomes loud, add a "show only Holocron additions" toggle on the panel.
3. **Markers go stale if the enrichment is superseded between the read and the render.** Acceptable — the read is consistent at one instant; a refresh after a daily pass shows the new state. The `kg.events` audit log tells the durable story.

## Verification

- Manual: open `/knowledge-graph`, expand Anakin Skywalker. Expect:
  - The "Titles" row tinted with secondary background (Augment marker — "Chosen One" added).
  - The `affiliated_with` chip with a secondary dot (annotated by Holocron).
  - All other rows/chips unchanged from today's rendering.
- Manual: open Yoda. Expect the `led` chip in `Color.Secondary` (Phase 2-only Add edge), and `occupations` row tinted (Add-property).
- Manual: open any node with no active enrichments. Expect the panel to look identical to current `main`.

## Open questions

1. Should the augment-marker show the actual added values (not just "+1")? Defer to Stage E1.5 — needs a design pass on the per-value provenance, which the current `kg.enrichments.value` shape supports but the merged view doesn't yet expose per-list-item.
2. Should clicking a marker deep-link to `/holocron?pageId=X&fieldPath=Y`? Probably yes, but adds another query parameter to the Holocron Log page filter — tracked separately.
3. Theme variants: the secondary colour palette differs across faction themes. Verify Sith / Jedi / Mandalorian themes still produce a recognisable Phase 1 vs Phase 2 contrast.
