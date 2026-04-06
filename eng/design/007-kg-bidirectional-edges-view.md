# Design-007: KG Bidirectional Edges View + Planned `QueryGraphAsync` Rewrite

**Status:** Partially Shipped (view landed; `QueryGraphAsync` rewrite pending)
**Date:** 2026-04-05
**Author:** Patrick Magee + Claude

## Problem

`KnowledgeGraphQueryService.QueryGraphAsync` is the workhorse of KG neighborhood exploration — it powers:

- The `traverse_graph` AI tool (Ask AI agent)
- The Graph Explorer page (`/kg/graph`)
- The Knowledge Graph page (`/kg`)
- The `render_graph` component tool

It performs a **mixed-direction BFS** in C#: at every hop it issues two `Find()` queries against `kg.edges` (outgoing `{fromId: {$in: frontier}}` and inbound `{toId: {$in: frontier}}`), then deduplicates, rewrites inbound labels to their reverse form, filters by realm/temporal/continuity, and expands the frontier. For `maxDepth=3` this is 6 round-trips plus a final node-enrichment query — roughly 7 sequential driver calls for a single traversal.

Per [ADR-003](../adr/003-kg-query-architecture.md), this is the right shape for mixed-direction traversal on the current schema. But the round-trip count and the hop-by-hop frontier management are visible on hub nodes (Anakin, Palpatine, the Galactic Republic), and the manual BFS forecloses some capabilities that `$graphLookup` provides for free:

- `depthField` — every returned edge tagged with its hop distance, usable for UI rendering (concentric rings, opacity by depth).
- Single-round-trip execution — `restrictSearchWithMatch` pushes label/continuity/temporal pruning into one aggregation.
- Traversal cycle prevention — `$graphLookup` handles revisits automatically; our BFS does so via an app-side `visited` set.

We cannot use `$graphLookup` directly against `kg.edges` because it recurses in **one direction per invocation**. A forward `$graphLookup` from Anakin finds his masters and their masters; it misses his apprentices (inbound `apprentice_of`) and anyone reachable through mixed chains like "Anakin → Obi-Wan (master) ← Luke (apprentice of Obi-Wan)". Two parallel `$graphLookup`s — one forward, one reverse — capture direction-pure chains but still miss the mixed ones.

**The unlock** is to normalize the edges collection into a **bidirectional view** where each edge appears twice: once as-stored (forward branch) and once flipped with `from`/`to` swapped and the label replaced by its reverse form (reverse branch). A single `$graphLookup` against this view performs mixed-direction traversal in one pipeline — because in the view, "mixed direction" becomes "pure forward over a bigger edge set".

## Shipped (April 2026)

### 1. `reverseLabel` denormalization on `kg.edges`

Added a new field to `RelationshipEdge`:

```csharp
[BsonElement("reverseLabel"), BsonIgnoreIfNull]
public string? ReverseLabel { get; set; }
```

Populated in `InfoboxGraphService.ProcessAsync` during the post-processing loop that already enriches `ToType`/`ToRealm`. The source is a single lookup map built from `FieldSemantics.Relationships`:

```csharp
var reverseLabelMap = FieldSemantics.Relationships.Values
    .DistinctBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(d => d.Label, d => d.Reverse, StringComparer.OrdinalIgnoreCase);

// Per-edge enrichment:
if (reverseLabelMap.TryGetValue(edge.Label, out var revLabel) && !string.IsNullOrEmpty(revLabel))
    edge.ReverseLabel = revLabel;
```

On dev, 582,051 / 595,159 edges (97.8%) carry a `reverseLabel`. The remaining 2.2% are genuine one-way labels (self-referential, terminal, or not yet mapped in `FieldSemantics`) — these are intentionally excluded from the reverse branch of the view so it never surfaces ambiguous labels.

### 2. The `kg.edges.bidir` view

Created by `InfoboxGraphService.EnsureBidirectionalEdgesViewAsync`, called at the end of Phase 5:

```javascript
db.createView("kg.edges.bidir", "kg.edges", [
  // Forward branch: every edge as-stored, annotated with direction="forward"
  { $addFields: { direction: "forward" } },

  // Reverse branch: $unionWith another scan of kg.edges with fields flipped
  { $unionWith: {
      coll: "kg.edges",
      pipeline: [
        // Drop edges without a reverse label — they're one-way
        { $match: { reverseLabel: { $exists: true, $nin: [null, ""] } } },
        { $project: {
            _id: 1,
            fromId:   "$toId",
            toId:     "$fromId",
            fromName: "$toName",
            toName:   "$fromName",
            fromType: "$toType",
            toType:   "$fromType",
            fromRealm:"$toRealm",
            toRealm:  "$fromRealm",
            label:    "$reverseLabel",
            weight: 1, evidence: 1, continuity: 1,
            fromYear: 1, toYear: 1, sourcePageId: 1,
            direction: { $literal: "reverse" }
        }}
      ]
  }}
])
```

**View statistics on dev:**

| Metric | Value |
| --- | --- |
| Forward branch (base `kg.edges`) | 595,159 edges |
| Reverse branch (after `reverseLabel` filter) | 582,051 edges |
| Total view documents | 1,177,210 |
| Edges dropped from reverse branch | 13,108 (one-way labels) |

The view is lifecycle-managed by `EnsureBidirectionalEdgesViewAsync`: drops any existing view, then runs `createView` via `RunCommandAsync`. Called at the end of every Phase 5 run so the definition stays in sync with any changes to `FieldSemantics.Relationships` that flow through the `reverseLabel` denormalization.

### 3. Smoke-test results

A 2-hop bidirectional `$graphLookup` from Anakin Skywalker (PageId 452390) filtered to `apprentice_of` + `master_of` labels, Canon continuity:

```javascript
db["kg.nodes"].aggregate([
  { $match: { _id: 452390 } },
  { $graphLookup: {
      from: "kg.edges.bidir",
      startWith: "$_id",
      connectFromField: "toId",
      connectToField: "fromId",
      as: "network",
      maxDepth: 1,
      depthField: "d",
      restrictSearchWithMatch: {
        label: { $in: ["apprentice_of", "master_of"] },
        continuity: "Canon"
      }
  }}
])
```

**Result: 127 edges returned in 48 ms.** The result mixes forward and reverse branches seamlessly:

- d=0 forward: Anakin → Qui-Gon, Anakin → Obi-Wan, Anakin → Darth Sidious (`apprentice_of` — his masters)
- d=0 reverse: Darth Vader → Fourth Sister, Darth Vader → Sixth Brother, Darth Vader → Eleventh Brother (`master_of` — his apprentices, via the `apprentice_of` ↔ `master_of` reverse pair)
- d=1 expansion: Obi-Wan → Luke, Yoda's apprentices, Darth Sidious → Darth Plagueis, Saw Gerrera → Jyn Erso, and so on

Single round-trip, cycle prevention handled automatically, depth annotation free. This is the exact capability the manual BFS was built to replicate by hand.

## Planned: Rewrite `QueryGraphAsync` over the view

The view exists and works. The next piece of work is **replacing the manual hop-by-hop BFS in `QueryGraphAsync` with a single `$graphLookup` against `kg.edges.bidir`**.

### Proposed pipeline shape

```csharp
var pipeline = new BsonDocument[]
{
    new("$match", new BsonDocument(MongoFields.Id, pageId)),
    new("$graphLookup", new BsonDocument
    {
        { "from", "kg.edges.bidir" },
        { "startWith", "$_id" },
        { "connectFromField", RelationshipEdgeBsonFields.ToId },
        { "connectToField",   RelationshipEdgeBsonFields.FromId },
        { "as", "network" },
        { "maxDepth", maxDepth - 1 }, // 0-indexed
        { "depthField", "d" },
        { "restrictSearchWithMatch", BuildRestrictMatch(
            labelFilter, continuity, yearFrom, yearTo, realmFilter
        )}
    }),
    new("$project", new BsonDocument
    {
        { MongoFields.Id, 1 },
        { GraphNodeBsonFields.Name, 1 },
        { "network._id", 1 },
        { "network.fromId", 1 }, { "network.toId", 1 },
        { "network.fromName", 1 }, { "network.toName", 1 },
        { "network.fromType", 1 }, { "network.toType", 1 },
        { "network.label", 1 }, { "network.weight", 1 },
        { "network.fromYear", 1 }, { "network.toYear", 1 },
        { "network.direction", 1 }, { "network.d", 1 }
    })
};
```

**The reverse-branch edges are already relabeled and flipped inside the view.** Post-processing in C# collapses to:

1. Dedupe by `(_id, direction)` — edges appear in both branches, which is the whole point, so dedup is actually on view-doc identity.
2. Cap the result count (`.Take(200)` matching the current behavior).
3. Load node metadata for the set of touched `fromId`/`toId` values in one `kg.nodes` `Find` for `ImageUrl` enrichment (types/names are already on the edges).

### Filter mapping: every current filter has a server-side home

| Current `QueryGraphAsync` filter | New form in `restrictSearchWithMatch` |
| --- | --- |
| `outgoingLabelFilter` (forward labels) + `inboundLabelFilter` (reverse labels mapped back to forward) | Single `{label: {$in: [...all requested labels...]}}` — the view already has reverse-branch edges labeled with their reverse form, so the client's "commanded" request matches both "Anakin commanded 501st" (forward) and "Battle of X commanded_by Anakin → Anakin commanded Battle of X" (reverse-branch relabeling) |
| `continuity` equality | `{continuity: "Canon"}` — unchanged |
| `temporalFilter` (from-year ≤ yearTo AND to-year ≥ yearFrom, null-tolerant) | Same clause — `fromYear`/`toYear` survive both view branches |
| `outRealmFilter` / `inRealmFilter` (accept field-absent OR in realm set) | Same clause on `toRealm` — the view swaps realms in the reverse branch so the "new endpoint realm" is always `toRealm` in the view-doc |

The single-filter expression is possible because the view normalizes direction: for every view-doc, the "new node reached by this edge" is always `toId` (forward branch: obviously; reverse branch: the flipped `toId` which was the original `fromId`). That's the key structural property that makes the view pay for itself.

### What the rewrite removes

- `forwardToReverse` / `reverseToForward` dictionaries for per-request label mapping — no longer needed, the view holds this mapping as data.
- `outgoingLabelFilter` / `inboundLabelFilter` duplication — collapse to one set.
- Hop-by-hop `for (var depth = 0; depth < maxDepth; ...)` loop with frontier management, visited set, and next-frontier expansion.
- The parallel outgoing/inbound `Find()` calls and their `.Limit(500)` per-hop caps.
- Inbound edge relabeling code (flip `fromId`/`toId`, map `origLabel` to its reverse).
- The `DistinctBy((e.from, e.to, e.label))` final dedup — replaced with dedup on view-doc `_id + direction`.

Net LOC reduction: ~100 lines of filter construction + frontier management → ~30 lines of pipeline construction + flat result processing.

### Known risks and open questions

1. **View pipeline re-evaluation on every `$graphLookup` hop.** MongoDB evaluates the view's underlying pipeline for each recursive depth. For `maxDepth=3` that is 3 evaluations of a `$unionWith` over 595k base edges = ~1.8M doc passes. The smoke test showed 48 ms on a 1-hop traversal, but scaling behavior at 3-4 hops needs benchmarking against real workloads before we flip the Graph Explorer page over.
   - **Mitigation path:** if the view-based rewrite is too slow at higher depths, upgrade `kg.edges.bidir` from a view to a **materialized collection** rebuilt during Phase 5. The shape stays identical (code doesn't change), only the storage backing does. The base `kg.edges` then owns indexes on `(fromId, label)`/`(toId, label)`; the materialized bidir collection gets a single `(fromId, label)` index because all edges are already in the correct direction.
2. **Indexes on the view's underlying collection.** MongoDB's query planner pushes `$graphLookup`'s per-hop `{ [connectToField]: seed }` predicate into the view pipeline. For the forward branch this becomes `kg.edges.find({fromId: seed, ...restrict})` which uses `ix_fromId_label`. For the reverse branch it becomes `kg.edges.find({toId: seed, ...restrict})` (because the view's reverse projection renames `toId` → `fromId`, which the planner rewrites) using `ix_toId_label`. **Both compound indexes landed as part of this work for exactly this reason.** Verify the planner is actually doing this with `explain()` before declaring victory on the rewrite.
3. **Result ordering.** The manual BFS returns edges in frontier-expansion order (roughly BFS order). `$graphLookup` returns them in traversal order which MAY differ at depths > 1. The Graph Explorer UI doesn't depend on ordering but the AI agent's tool result serialization might — audit `GraphTraversalDto` shape before the rewrite.
4. **`onlyRoot` early-exit behavior.** The frontend's "None" button toggles all labels off and requests only the root node. Current code has a dedicated early exit for this that bypasses the BFS entirely. The rewrite must preserve this because sending `labels: []` to `restrictSearchWithMatch` would match nothing — which IS correct but wastes a round trip. Keep the early exit unchanged.
5. **Edge weight and evidence fidelity.** The reverse branch's edges carry the same `weight`/`evidence` as their forward counterparts. UI that surfaces "this edge has weight 0.95 with evidence X" sees the same weight whether the edge came in forward or reverse form. This is correct (it is the same edge, just read from a different perspective) but worth confirming nothing displays the evidence as being from the wrong entity's article.

### When to do the rewrite

Not urgent. The current manual BFS is correct, well-tested, and performs adequately at the current graph scale. Trigger conditions for the rewrite:

- The Graph Explorer UI adds a depth slider at 4+ hops and the manual BFS starts timing out.
- The AI agent adds a tool that needs `depthField` output for rendering (e.g. "show me the 3-hop trust network weighted by depth").
- A benchmark on a hub node (Palpatine, Galactic Republic) shows the manual BFS exceeds 500ms — at which point the view's 48ms / hop extrapolates favorably even with re-evaluation cost.
- We upgrade to a materialized `kg.edges.bidir` collection for other reasons (analytics, export), at which point the rewrite is nearly free.

## Files touched so far

| File | Change |
| --- | --- |
| [src/StarWarsData.Models/KnowledgeGraph/RelationshipEdge.cs](../../src/StarWarsData.Models/KnowledgeGraph/RelationshipEdge.cs) | Added `ReverseLabel` field |
| [src/StarWarsData.Models/KnowledgeGraph/RelationshipEdgeBsonFields.cs](../../src/StarWarsData.Models/KnowledgeGraph/RelationshipEdgeBsonFields.cs) | Added `ReverseLabel` BSON field constant |
| [src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) | `reverseLabelMap`, per-edge enrichment, `EnsureBidirectionalEdgesViewAsync` |
| ADR-003 ([`eng/adr/003-kg-query-architecture.md`](../adr/003-kg-query-architecture.md)) | Documents denormalization strategy and index ownership |

## Files to touch during the planned rewrite

| File | Change |
| --- | --- |
| `src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs` | Replace `QueryGraphAsync` body (~200 LOC) with single `$graphLookup` pipeline + flat result processing |
| `src/StarWarsData.ApiService/Features/KnowledgeGraph/RelationshipGraphController.cs` | No changes expected — signature stays the same |
| `src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs` | No changes expected — `TraverseGraph` calls `QueryGraphAsync` by the same signature |
| `src/StarWarsData.Tests/RelationshipGraphPipelineTests.cs` | Add a test that asserts mixed-direction traversal from a known hub returns both forward and reverse edges in a single call |
| `src/StarWarsData.Tests/RelationshipGraphPipelineTests.cs` | Add a benchmark test marking the expected performance envelope (e.g. 3-hop from Anakin < 250ms) |

## Open items

- **Indexes on the view.** MongoDB views themselves cannot have their own indexes — they use the underlying collection's indexes via pipeline rewrite. Confirm via `db["kg.edges.bidir"].aggregate([...]).explain("executionStats")` that both branches of the view hit `ix_fromId_label` and `ix_toId_label` respectively for typical traversal queries.
- **Prod rollout.** The view creation is idempotent (drop + create) and runs at the end of every Phase 5. For prod, no manual step is needed beyond rerunning Phase 5, which is already scheduled daily via Hangfire. The `reverseLabel` denormalization lands on the same rerun.
- **`EdgeMeta` on the reverse branch.** The view's reverse `$project` does not include `meta`. This is intentional for now (the qualifier text is source-side) but worth flagging if the UI starts surfacing edge metadata from both perspectives.
