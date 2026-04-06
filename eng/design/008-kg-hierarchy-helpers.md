# Design-008: KG Hierarchy Helpers — Precomputed Lineage Closures on `kg.nodes`

**Status:** Shipped (code + dev-populated + verified)
**Date:** 2026-04-05
**Author:** Patrick Magee + Claude

## Problem

[ADR-003](../adr/003-kg-query-architecture.md) identified one real gap against MongoDB's data-modeling guidance: the [tree structures modelling guidance](https://www.mongodb.com/docs/manual/applications/data-models-tree-structures/) recommends **materialized ancestor arrays** for tree/DAG-shaped subgraphs, turning N-hop ancestor traversal into a single-field read and `is-descendant-of` into a `$in` check.

Our graph contains several genuinely tree-shaped subsets that today are walked at query time via `GetLineageAsync` (`$graphLookup` with a single label, single direction). At current scale this is fast (~48ms for 5-hop traversal), but it burns a tool call on the AI agent for every "who are X's ancestors?" question, and the algorithmic shape is wrong for the read pattern — these chains never change between ETL runs, so recomputing them at query time is redundant work.

## Solution

Precompute the **transitive closure** of each registered tree-shaped lineage once per Phase 5 ETL run and embed it on `kg.nodes` as `lineages.<key>`: an ordered list of `PageId`s reachable from the seed node by walking that label in that direction. A wildcard index `lineages.$**` makes membership queries O(1) without needing a dedicated index per lineage.

This matches the same cost/benefit calculus that justified denormalizing `fromRealm`/`toRealm`/`reverseLabel` on edges in ADR-003 — precompute once per ETL, read thousands of times per day, stale-resolution via full rebuild.

## Design decisions

### 1. Registry-driven, not ad-hoc

Lineages are defined in [`HierarchyRegistry.Lineages`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/HierarchyRegistry.cs) as a list of `(Label, Direction, LineageKey, Description)` records. Adding a new tree-shaped label is a one-entry change; the ETL iterates over the registry.

This mirrors how `FieldSemantics.Relationships` drives other ETL behavior (label classification, reverse-label denormalization) and makes the decision to include/exclude a label explicit and reviewable.

### 2. Direction is mechanical, not semantic

`LineageDirection.Forward` walks along stored edge direction (`fromId → toId`); `Reverse` walks against it (`toId → fromId`). This is the same convention `GetLineageAsync` uses, and for the same reason: the semantic meaning of "ancestors" depends on the label's own direction.

- `apprentice_of` edges point apprentice → master. **Forward** walks towards masters (= ancestors in the lineage tree).
- `child_of` edges point child → parent. **Forward** walks towards parents (= ancestors).
- `in_region` edges point planet → region. **Forward** walks towards the containing region (= ancestors in the containment tree).

For the current registry, every entry uses Forward. Reverse is available for future entries where the canonical label is stored in the opposite direction (e.g. if a `predecessor_of` label is added where the edge points newer → older and you want "all predecessors" = walk forward, or `successor_of` where the edge points older → newer and you want "all predecessors" = walk reverse).

### 3. Which labels are included

Surveyed on `starwars-dev` on 2026-04-05 against all candidate labels. Final registry:

| Label | Direction | `lineageKey` | Edges | Populated nodes | Notes |
| --- | --- | --- | --- | --- | --- |
| `apprentice_of` | Forward | `apprentice_of` | 1,531 | 1,225 | Jedi/Sith lineage. 2 direct cycles (chars trained each other) — handled by visited set |
| `child_of` | Forward | `ancestors` | 1,905 | 1,316 | Family tree (DAG, up to 2 parents). `parent_of` intentionally excluded to avoid double-count |
| `in_system` | Forward | `in_system` | 7,962 | 8,991 | Planet → star system |
| `in_sector` | Forward | `in_sector` | 7,103 | 16,899 | System/planet → sector |
| `in_region` | Forward | `in_region` | 13,318 | 22,763 | Sector/planet → region (4 region variants per planet on average due to Outer Rim naming) |
| `part_of` | Forward | `part_of` | 132 | 120 | Publication hierarchy (comic issue → series) |

**Explicitly excluded**:

- **`sibling_of`** — self-reversing label, forms cliques not trees. Would produce incorrect "ancestor" semantics.
- **`parent_of`** — mutual reverse of `child_of`; both are written to `kg.edges` by Phase 6 LLM pair-write, so using both would double-count. `child_of` is chosen because its forward direction gives ancestors naturally ("the chain of my parents").
- **`member_of`** — multi-parent over time (characters belong to many orgs), not tree-shaped.
- **`successor_of` / `predecessor_of`** — not present in `kg.edges` on dev (0 edges).

**Planetary containment uses three stacked labels** (`in_system`, `in_sector`, `in_region`) rather than a single `part_of` because Wookieepedia infoboxes link planets to whichever level has data in the article. A well-documented planet like Tatooine has all three closures populated (Tatoo system / Arkanis sector / Outer Rim Territories); a sparsely-documented planet may have only one.

### 4. Closure algorithm

Implemented in `InfoboxGraphService.ComputeLineageClosures`. For each registered lineage:

1. Build a one-hop adjacency map `Dictionary<int, List<int>>` keyed by seed id. Forward direction: key = `fromId`, value list appends `toId`. Reverse direction: swapped.
2. For each seed node in the adjacency map, run a BFS:
   - `visited` set initialized with the seed id itself
   - Queue starts with seed id
   - Dequeue → look up neighbours → for each unvisited neighbour, add to closure list + visited set + queue
   - Terminates when queue empty (closure is complete)
3. The closure list is assigned to `node.Lineages[lineage.LineageKey]` on the matching `GraphNode`.

**Cycle safety**: the `visited` set guarantees each ancestor appears exactly once regardless of how many paths reach it. The two known `apprentice_of` cycles on dev (Dooku → Sidious → Plagueis → ... → Dooku and similar) are handled transparently — the cycle-closing edge finds an already-visited node and is skipped.

**Ordering**: BFS queue semantics produce a closure ordered by hop distance (nearest ancestors first, farthest last). This is preserved through the MongoDB insert and matches the natural "nearest first" display order callers expect.

**Cost**: O(edges × seed count × average chain depth) per lineage. The largest label on dev (`in_region` at 13k edges populating 22k nodes) takes ~4.6 seconds via the $graphLookup-based dev validation; the in-memory BFS in C# will be faster because it avoids per-seed aggregation round-trips.

### 5. Storage: embed on `kg.nodes`, not a separate collection

Considered alternatives:

- **Separate `kg.lineages` collection** keyed by `(pageId, lineageKey)`. Cleaner separation, easier to rebuild lineages independently of nodes, but adds a `$lookup` on every read that wants both node metadata and lineage data.
- **Embed on `kg.nodes` as `lineages` subdocument**. Matches MongoDB's bounded-helper-field guidance, no extra reads, atomic per-node writes.

Chose embedding because:

- Lineage closures are small and bounded (largest observed: ~30 elements for deep Jedi chains or nested regions). Well within the guide's "bounded substructure" criterion.
- Phase 5 ETL is already rewriting `kg.nodes` on every run, so adding a field per node has zero extra write cost.
- The AI agent tools that will consume these lineages typically also need node metadata (name, type) in the same call — a single read is cheaper than a $lookup.

### 6. Indexing: wildcard over `lineages.$**`

Instead of creating one index per registered lineage (6 today, likely more in the future), a single **wildcard index** on `lineages.$**` covers every current and future lineage key. MongoDB's wildcard index traverses all paths under the prefix and indexes each terminal value.

Verified on dev via `explain("executionStats")`:

```
Query: { "lineages.in_region": 469537 }  // Core Worlds region id
Stage: FETCH
indexName: ix_lineages_wildcard
totalKeysExamined: 1788
totalDocsExamined: 1788
nReturned: 1788
executionTimeMillis: 2
```

`keysExamined == nReturned` = perfectly selective index seek. 1,788 nodes in Core Worlds region returned in 2ms. No per-lineage indexes needed.

Trade-off: wildcard indexes are slightly larger than targeted indexes and cannot be compound (can't combine with `{continuity: 1}` for example). For our read pattern (point membership queries) this isn't a limitation.

## Verification on `starwars-dev`

The ETL code is written and builds clean. To validate correctness and performance before the next scheduled Phase 5 rerun, I populated all 6 lineages against the live dev collection using an equivalent `$graphLookup` + `$merge` pipeline (one pass per lineage, using the aggregation-pipeline form of `whenMatched` with `$$new.closure` to `$set` a specific subfield without replacing the parent `lineages` doc).

**Results:**

```
apprentice_of    ->   1,225 nodes in 2,392 ms
ancestors        ->   1,316 nodes in 2,391 ms
in_system        ->   8,991 nodes in 2,932 ms
in_sector        ->  16,899 nodes in 3,727 ms
in_region        ->  22,763 nodes in 4,670 ms
part_of          ->     120 nodes in 2,278 ms
```

Total: ~18 seconds for full-collection lineage computation. The C# in-memory version during Phase 5 will be faster (no per-seed aggregation round-trip).

**Correctness spot-checks:**

| Seed | Lineage | Expected | Actual |
| --- | --- | --- | --- |
| Anakin Skywalker (452390) | `apprentice_of` | Masters chain: Obi-Wan, Qui-Gon, Yoda, Sidious, Dooku, Plagueis, Tenebrous, N'Kata Del Gormo, Garro, Yoda's old Master | ✅ all 10 present |
| Tatooine (452688) | `in_system` / `in_sector` / `in_region` | Tatoo system / Arkanis sector / Outer Rim Territories | ✅ exactly these, plus 3 region variants (The Slice, Galactic Frontier, Occlusion Zone) |
| Luke Skywalker (452217) | `ancestors` (from `child_of`) | Parents/grandparents via in-universe family tree | ✅ closure populated (4 entries) |

**Performance on membership queries:**

| Query | Matches | Time |
| --- | --- | --- |
| All transitive apprentices of Yoda | 20 (limited) | 1 ms |
| All nodes in Core Worlds region | 1,788 | 2 ms |
| All nodes in Tatoo system | 22 | 2 ms |

All queries use `ix_lineages_wildcard` per `explain()`.

## Planned: new AI agent tools backed by lineages

The lineages are now the right substrate for two new `GraphRAGToolkit` tools that don't yet exist:

### `is_descendant_of(entityId, ancestorId, lineageKey)`

Returns a boolean via a single `kg.nodes` read:

```csharp
var node = await _kg.GetNodeByIdAsync(entityId);
return node?.Lineages.TryGetValue(lineageKey, out var closure) == true
    && closure.Contains(ancestorId);
```

Powers questions like:

- "Is Luke Skywalker a descendant of Shmi?" (`lineageKey = "ancestors"`)
- "Is Tatooine in the Outer Rim Territories?" (`lineageKey = "in_region"`)
- "Was Ahsoka trained by Yoda's lineage?" (`lineageKey = "apprentice_of"`)

Currently the agent would call `get_lineage` and then scan the result in the LLM context — a wasted tool call round-trip for a boolean answer.

### `get_ancestors(entityId, lineageKey)`

Returns the full closure array in one read, enriched with node names/types:

```csharp
var node = await _kg.GetNodeByIdAsync(entityId);
if (!node.Lineages.TryGetValue(lineageKey, out var closure)) return [];
var ancestors = await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, closure)).ToListAsync();
// Preserve closure order (nearest first)
var byId = ancestors.ToDictionary(a => a.PageId);
return closure.Select(id => byId[id]).ToList();
```

Powers questions like:

- "Who are all of Yoda's masters transitively?" (single read, instant)
- "What region, sector, and system is Tatooine in?" (combine three lineageKeys)
- "List all of Vader's ancestors in the Jedi lineage" (walks back to the pre-Plagueis chain)

### `get_descendants_of(ancestorId, lineageKey, type?)`

Inverse direction via the wildcard index:

```csharp
var filter = Builders<GraphNode>.Filter.AnyEq($"{GraphNodeBsonFields.Lineages}.{lineageKey}", ancestorId);
if (type is not null) filter &= Builders<GraphNode>.Filter.Eq(n => n.Type, type);
return await _nodes.Find(filter).ToListAsync();
```

Powers questions like:

- "Who are all of Yoda's apprentices transitively?" (currently requires a reverse `get_lineage` call)
- "What planets are in the Outer Rim?" (instant — 1,788 results in 2ms per the smoke test)
- "What characters are descended from Shmi Skywalker?"

## Files touched

| File | Change |
| --- | --- |
| [src/StarWarsData.Services/KnowledgeGraph/Definitions/HierarchyRegistry.cs](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/HierarchyRegistry.cs) | New file — registry of tree-shaped labels |
| [src/StarWarsData.Models/KnowledgeGraph/GraphNode.cs](../../src/StarWarsData.Models/KnowledgeGraph/GraphNode.cs) | Added `Lineages` field (`Dictionary<string, List<int>>`) |
| [src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) | Added `ComputeLineageClosures` method, called after edge dedup + before node insert; added `ix_lineages_wildcard` index |
| [eng/adr/003-kg-query-architecture.md](../adr/003-kg-query-architecture.md) | Added Gap 1 section referencing this design |

## Files to touch when the new AI tools land

| File | Change |
| --- | --- |
| `src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs` | Add `IsDescendantOf`, `GetAncestors`, `GetDescendantsOf` tool methods + DTOs |
| `src/StarWarsData.Services/AI/Toolkits/ToolkitDtos.cs` | Add `LineageMembershipDto`, `AncestorListDto`, `DescendantListDto` |
| `src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs` | Add `IsInLineageAsync`, `GetAncestorsAsync`, `GetDescendantsAsync` query methods |
| `src/StarWarsData.Tests/...` | Add membership-query tests using known Jedi lineages and planetary containment |

## Open items

- **Lineage keys don't enforce uniqueness at compile time.** `HierarchyRegistry.Lineages` is a runtime list; if two entries share a `LineageKey` the later one overwrites the earlier in `node.Lineages`. Trivially fixable via a startup assertion or by deriving `LineageKey` from the label when no explicit override is given. Low priority — current registry has 6 distinct keys.
- **`ancestors` is an aliased key.** The `child_of` lineage uses `LineageKey = "ancestors"` as a more natural read-side name ("who are X's ancestors?") rather than `"child_of"` (which would invite the confusing "child_of closure" phrasing). This aliasing is fine but must be documented wherever consumers reference the key — the registry is the source of truth.
- **Prod rollout.** Code is self-contained in Phase 5. Running the next daily ETL refresh will populate closures automatically. No data migration needed; the `$exists: false` filter back-compat pattern from ADR-003 also applies here — queries against pre-ETL-refresh nodes will silently skip the lineage check.
- **Storage cost.** Each lineage entry is 4 bytes (int32). Largest per-node closure seen on dev is ~30 entries = 120 bytes. Across 138k nodes averaging 2 lineages each at ~10 entries, total added storage is ~11 MB. Negligible against the base collection.
- **Tool description quality.** When the new tools land, their `[Description]` text must make the direction and lineageKey semantics clear — particularly that "ancestors" in this registry means biological ancestors (from `child_of`), not a generic "all ancestors" across arbitrary labels.

## Related

- [ADR-003: KG Query Architecture](../adr/003-kg-query-architecture.md) — §Gaps vs MongoDB best practices §Gap 1
- [Design-007: KG Bidirectional Edges View](./007-kg-bidirectional-edges-view.md) — sibling work on `reverseLabel` denormalization
- [MongoDB tree structures guidance](https://www.mongodb.com/docs/manual/applications/data-models-tree-structures/) — the source recommendation for ancestor-array patterns
- [MongoDB wildcard indexes](https://www.mongodb.com/docs/manual/core/indexes/index-types/index-wildcard/) — backing index for `lineages.$**`
