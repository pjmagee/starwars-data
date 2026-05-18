# 021 — Edge bound provenance

Status: Shipped (2026-04-28, `5a3506646d`). All phases landed: `EdgeBoundsSource` enum + `EdgeMeta.BoundsSource`, write-side tagging at both sites (`NodeBuilderBase.cs` → Infobox, `InfoboxGraphService.cs` → Lifecycle), `HolocronAgent` FillGap refinable check + `[infobox, hard]`/`[lifecycle, refinable]` prompt annotation, read-side merge in `KnowledgeGraphQueryService`, and retroactive Migration 0015 (`0015-edge-bounds-source-tagging.js`).
Date: 2026-04-27
Author: Patrick Magee
Cross-refs: [Design-001 — Temporal facets](001-temporal-facets.md), [Design-002 — Edge quality](002-edge-quality.md), [Design-018 — KG enrichments architecture](018-kg-enrichments-architecture.md), [Design-020 — Async Holocron pipeline](020-holocron-async-pipeline.md), [ADR-006 — long-running AI workflow pipelines](../adr/006-long-running-ai-workflow-pipelines.md)

## Problem

The KG has `kg.edges.fromYear` and `kg.edges.toYear` fields populated by Phase 5
ETL. Today they have **two different sources** with **identical wire shape**:

1. **Infobox-supplied** — the wiki value parser found years in the parenthetical
   qualifier (e.g. `"Darth Vader (19 BBY – 4 ABY, Sith Lord)"`). These are
   genuine bounds backed by source text. Rare in practice — most infobox values
   don't carry per-edge dates.

2. **Lifecycle-fallback** — Phase 5 derived the bound by intersecting the two
   endpoint nodes' lifespans
   (`from = max(src.startYear, tgt.startYear)`, `to = min(src.endYear, tgt.endYear)`).
   Implemented in
   [InfoboxGraphService.cs:235-277](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L235-L277).
   Correct as an **upper bound** (the relationship cannot exist outside the
   intersection of the endpoints' lifetimes), but **loose** for any relationship
   that's a sub-span of either endpoint's life.

The conflation is the bug. `kg.edges` exposes a single `int? fromYear` field
with no provenance. Downstream code can't tell a hard, evidence-backed bound
from a soft, derived upper bound and treats them identically.

### Concrete failure case

```
Anakin Skywalker (PageId 452390)  →[apprentice_of]→  Darth Sidious (PageId 452582)
fromYear: -41   ← Anakin's BIRTH year
toYear:    4    ← Anakin's DEATH year
meta: { qualifier: "Sith Master" }
```

Anakin was 0 years old in 41 BBY. The Sith apprenticeship begins at Mustafar
(~19 BBY) and ends on Death Star II (~4 ABY). The `fromYear=-41` is the
lifecycle-intersection upper bound — correct as "the relationship can't exist
before Anakin existed," wrong as "the relationship started in 41 BBY." The
year-slider on the D3 graph (`filterByYear` in
[d3-graph.js:601-632](../../src/StarWarsData.Frontend/wwwroot/js/d3-graph.js#L601-L632))
treats this as a hard bound and shows the apprentice edge active when
Anakin is 5 years old.

### Why Holocron can't fix it today

The Phase 2 Holocron agent has a `FillGapEdge` operation specifically for
populating null temporal bounds. Its pre-flight check at
[HolocronAgent.cs:1207-1210](../../src/StarWarsData.Services/AI/Agents/HolocronAgent.cs#L1207-L1210)
explicitly forbids overwriting non-null bounds: *"we never let FillGap overwrite
a non-null bound."*

That check is correct in spirit (don't let the agent stomp on infobox-sourced
truth) but wrong in practice (it also forbids refining lifecycle-derived soft
upper bounds). FillGap is starved on these edges — it can't propose
`fromYear=-19` because Phase 5 already filled `fromYear=-41`.

### Why this generalizes beyond characters

The lifecycle-fallback shape applies to every `(FromType, ToType)` pair where
one endpoint outlives the relationship's actual span. A non-exhaustive list:

| From → To | Relationship | Sub-span? |
|---|---|---|
| Character → Character | `apprentice_of`, `married_to`, `mentored_by` | Yes |
| Character → Organization | `member_of`, `served_in`, `held_rank` | Yes |
| Character → TitleOrPosition | `held_position` | Yes |
| Battle → Campaign | `included_in` | Yes (battle is a moment) |
| Planet → Faction | `controlled_by` | Yes (planet outlasts factions) |
| Government → Government | `succeeded_by` | Yes (transition event, not a span) |
| Vehicle → Battle | `participated_in` | Yes (vehicle service life >> battle) |
| Character → Species | `species` | No (lifelong) |
| Character → Planet | `homeworld` | Usually lifelong |

The "hardcoded label allowlist" approach we considered first is a brittle proxy
for the real distinction: **how was the bound derived?** A label-list solution
fails the moment a new node type or label enters the corpus; a provenance-tag
solution doesn't.

## Goals

1. Make `kg.edges` honest about whether each bound is hard (infobox) or soft
   (lifecycle-derived).
2. Let the Holocron agent **refine** soft upper bounds with chunk-cited
   evidence — without changing the schema with a new operation type, and
   without a label-specific allowlist.
3. Keep the lifecycle fallback as a useful *upper bound* signal — don't lose
   it. It correctly excludes pre-Anakin years and post-Sidious years.
4. Survive Phase 5 re-runs and incremental ingestion: provenance gets
   re-tagged on every rebuild, and Holocron enrichments persist independently
   in `kg.edge_enrichments`.

## Non-goals

- **A `RefineEdge` operation.** With provenance, the existing `FillGap`
  operation does the same job, narrowed to lifecycle-tagged bounds. We keep
  the schema flat.
- **Letting Holocron correct genuine infobox-sourced bounds.** If the wiki is
  wrong, that's a separate problem with a much higher bar.
- **Per-edge calendar tagging (BBY/ABY/Demarcation, circa, uncertainty).** All
  bounds are still bare `int?` years on the canonical KG scale. See Design-001
  for the richer TemporalFacet model used at the node level — bringing that
  to edges is out of scope here.
- **Coupling activation to a Phase 5 re-run.** A migration retroactively tags
  the existing corpus on the next `MongoDbMigrations` container start
  (Migration 0015) — no operator-driven Phase 5 sweep required. Phase 5 from
  this point forward stamps tags at write time.

## Architecture

### Schema change

New enum:

```csharp
namespace StarWarsData.Models.Entities;

public enum EdgeBoundsSource
{
    /// <summary>Default — bounds are unset (both null) or provenance is unknown.</summary>
    Unknown = 0,

    /// <summary>The infobox value carried explicit years. Bounds are evidence-backed.</summary>
    Infobox = 1,

    /// <summary>Phase 5 derived the bound from endpoint lifecycle intersection. Soft upper bound.</summary>
    Lifecycle = 2,

    /// <summary>A Holocron run refined the bound (set on the kg.edge_enrichments value, not the base edge).</summary>
    Holocron = 3,
}
```

New field on `EdgeMeta`:

```csharp
public sealed class EdgeMeta
{
    // ...existing fields...

    /// <summary>
    /// How the edge's <c>fromYear</c>/<c>toYear</c> were determined. Distinguishes
    /// hard infobox-supplied bounds from soft lifecycle-fallback derivations so
    /// downstream code (Holocron pre-flight, query merge) can treat them appropriately.
    /// Absent (= Unknown) when both bounds are null.
    /// </summary>
    [BsonElement("boundsSource"), BsonIgnoreIfDefault]
    public EdgeBoundsSource BoundsSource { get; set; }
}
```

`BsonIgnoreIfDefault` keeps `Unknown` (= 0) off the wire so existing edges
stay compact; only edges with an actual bound carry the field.

### Phase 5 — write-side tagging

Two write sites both get the tag:

**1.** [`NodeBuilderBase.cs:247-273`](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs#L247-L273)
— when `pl.FromYear` or `pl.ToYear` is set (parsed from the infobox value's
qualifier), the edge's `Meta.BoundsSource = Infobox`. The current code
suppresses `Meta` to `null` when there's no qualifier/rawValue — this rule
broadens to "materialise Meta if any of qualifier, rawValue, order, OR a year
came from parsing."

**2.** [`InfoboxGraphService.cs:235-277`](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L235-L277)
— the lifecycle fallback block. When it fires (one of the three branches at
lines 247, 265, 271), set the edge's `Meta.BoundsSource = Lifecycle`,
materialising `Meta` if it was null.

Edges where neither path runs (no infobox years, no lifecycle data on either
endpoint) keep both `fromYear`/`toYear` null and `BoundsSource = Unknown`.

### Phase 2 — Holocron FillGap pre-flight

`IsFillGapValid` at
[HolocronAgent.cs:1195-1211](../../src/StarWarsData.Services/AI/Agents/HolocronAgent.cs#L1195-L1211)
loosens its rule, but **conservatively** — only edges explicitly tagged
`Lifecycle` (or with a null bound) are refinable. Untagged edges
(`Unknown`) are treated as hard until Migration 0015 / next Phase 5 sweep
gives them a real tag:

```csharp
// OLD: at least one bound must currently be NULL on the existing edge.
.Any(e => (prop.FromYear.HasValue && !e.FromYear.HasValue) || (prop.ToYear.HasValue && !e.ToYear.HasValue));

// NEW: at least one bound must currently be REFINABLE — null, or non-null
// with BoundsSource == Lifecycle. Unknown is treated as hard (conservative).
static bool IsBoundRefinable(RelationshipEdge edge, bool isFrom)
{
    var existing = isFrom ? edge.FromYear : edge.ToYear;
    if (!existing.HasValue) return true;
    return (edge.Meta?.BoundsSource ?? EdgeBoundsSource.Unknown) is EdgeBoundsSource.Lifecycle;
}
```

Why conservative on `Unknown`? Between deploy and Migration 0015 running,
existing edges with infobox-supplied bounds carry no provenance tag. If
`Unknown` were treated as refinable, the agent could overwrite a genuine
infobox bound during that gap. With `Unknown` treated as hard, the worst
case during the gap is "FillGap has nothing to refine" — strictly safer
than "FillGap clobbers infobox truth."

### Prompt rendering

The FillGap candidate list at
[HolocronAgent.cs:751-779](../../src/StarWarsData.Services/AI/Agents/HolocronAgent.cs#L751-L779)
expands its filter from "edges with any null bound" to "edges with any
**refinable** bound" (null OR `Lifecycle`-sourced).

Each candidate row gets the source annotated so the agent reasons correctly:

```
- fromId=452390 (Anakin Skywalker) —[apprentice_of]→ toId=452582 (Darth Sidious)
  (fromYear=-41 [lifecycle, refinable], toYear=4 [lifecycle, refinable]; fill or refine either or both)
```

vs

```
- fromId=A (Foo) —[X]→ toId=B (Bar)
  (fromYear=null, toYear=null; fill either or both)
```

The agent's instructions are updated to clarify: *"When a bound is marked
`[lifecycle, refinable]`, you may propose a tighter year if the chunks cite
when the relationship actually started/ended. When a bound is marked
`[infobox, hard]`, do not propose a year for it — use Annotate for context
instead."*

### Read side — query merge layers

Two paths overlay enrichments onto edges:

**1.** `KnowledgeGraphQueryService.GetEdgesForEntityAsync` (per-node detail
panel) — currently overlays only on null bounds. Update to overlay when null
**OR** `BoundsSource = Lifecycle`.

**2.** `KnowledgeGraphQueryService.QueryGraphAsync` (the D3 graph explorer
read path we wired in this session) — same update. The merge already runs
after the BFS and before re-applying the temporal filter; the only change is
the overlay condition.

When the overlay fires, the surfaced bound is the Holocron-refined value,
not the lifecycle bound. The `HolocronAppliedAt` / `HolocronJobId` /
`HolocronAgentVersion` fields already exposed by `EntityEdgeRowDto` give
the UI the provenance signal it needs to render `(refined)` or
`(approx)` annotations later if we want.

## Implementation phases

**Phase A — Schema + Phase 5 tagging.**
1. Add `EdgeBoundsSource` enum.
2. Add `EdgeMeta.BoundsSource` field with `[BsonIgnoreIfDefault]`.
3. Tag at both write sites (`NodeBuilderBase`, `InfoboxGraphService`).
4. Run Phase 5 on `starwars-dev`. All edges get re-tagged.

**Phase B — Holocron pre-flight + prompt.**
1. Loosen `IsFillGapValid` to accept `Lifecycle`-sourced bounds as refinable.
2. Update FillGap candidate filter (prompt-side) to include refinable edges.
3. Annotate prompt rows with `[lifecycle, refinable]` / `[infobox, hard]`.
4. Update agent instructions text to describe the new refinement rule.

**Phase C — Read-side merge.**
1. `GetEdgesForEntityAsync` — overlay-condition expanded to null OR
   `Lifecycle`.
2. `QueryGraphAsync` — same.

**Phase D — Verification.**
1. Build + Unit tests pass.
2. Spot check on `starwars-dev`: query the Anakin/Sidious edge — confirm
   `meta.boundsSource = "Lifecycle"` after Phase 5 re-run.
3. Run Holocron Enhance on Anakin. Verify the FillGap candidate list now
   includes `apprentice_of` (it didn't before).
4. If the agent proposes a tighter bound, verify the merge layer surfaces it
   on the per-node panel and on the D3 graph.

## Lessons learned (carry forward)

This change pays a recurring debt the codebase had built up: **storing
derived data with the same wire shape as source data**. The fix is a
provenance tag — small footprint, big downstream consequences. The pattern
will recur (e.g. Phase 1 wiki-extracted facts vs ETL-derived ones, Phase 7
territory-control derivations vs source-attested control), and the same
principle applies: **if downstream code might want to reason about the
data's confidence level, tag the source at write time.**

## Risks

- **Phase 5 re-run cost.** The lifecycle-fallback re-runs every edge. On
  694K edges this is minutes, not hours, but worth scheduling against the
  next planned ETL window in production.
- **Agent over-refinement.** The agent might propose tighter bounds based on
  weak evidence (a single chunk mentioning a year) when the lifecycle bound
  is actually correct (the relationship really did start at birth). Mitigation:
  the agent's instructions already require chunk-cited evidence; FillGap
  validation rejects proposals without it; Holocron enrichments are
  reversible (delete by JobId).
- **UI surface for `(approx)` indicator.** Optional polish — not in this
  design's scope but worth flagging for follow-up.

## Open questions

- Should we surface `BoundsSource` in the D3 graph viewer's edge tooltip so
  users can tell hard from soft bounds at a glance? Probably yes, separate
  PR.
- Is `Unknown` the right default name? Considered `Unset`. Going with
  `Unknown` for consistency with `Realm.Unknown` / other existing enum
  defaults.

## Verification

Acceptance criteria:

- [ ] `EdgeBoundsSource` enum exists and is referenced from `EdgeMeta`.
- [ ] After Phase 5 re-run on `starwars-dev`, the Anakin/Sidious
      `apprentice_of` edge has `meta.boundsSource: "Lifecycle"`.
- [ ] After Phase 5 re-run on `starwars-dev`, an edge with infobox-supplied
      years (sample one from a Battle infobox with `years_active`) has
      `meta.boundsSource: "Infobox"`.
- [ ] Running Holocron Enhance on Anakin includes the `apprentice_of` edge
      in the FillGap candidate list rendered to the LLM.
- [ ] The merge layer overlays a Holocron-refined bound onto a
      `Lifecycle`-sourced base bound (verified by manually inserting a test
      enrichment + querying through the API).
- [ ] Unit tests pass.
