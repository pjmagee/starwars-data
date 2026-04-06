# ADR-003: Knowledge Graph Query Architecture — MongoDB-Native with Denormalization

**Status:** Accepted
**Date:** 2026-04-05
**Decision maker:** Patrick Magee

## Context

The knowledge graph (`kg.nodes` + `kg.edges`) is the primary substrate for the Ask AI agent, the Graph Explorer page, the Knowledge Graph page, and the KG-backed analytics charts. It carries:

- ~138k nodes (entities: characters, planets, organizations, battles, ships, books…)
- ~600k edges after dedup (relationships: apprentice_of, commanded, member_of, parent_of…)
- Denormalized node metadata on every edge (`fromName`, `fromType`, `toName`, `toType`, plus — per this ADR — `fromRealm`, `toRealm`, `reverseLabel`)
- Temporal bounds on a growing subset of edges (`fromYear`, `toYear`, `EdgeMeta`)

Core read workloads against the graph include:

| Workload | Shape | Tool surface |
| --- | --- | --- |
| Direct relationships of an entity | 1-hop, in/out, label filter | `get_entity_relationships`, `GetAllEdgesForEntityAsync` |
| Broad neighborhood exploration | 2-4 hop, **mixed direction**, label filter | `traverse_graph`, `QueryGraphAsync` |
| Hierarchy traversal | N-hop, **single direction**, single label | `get_lineage`, `GetLineageAsync` |
| Shortest path | 2-4 hop bidirectional BFS, two endpoints | `find_connections`, `FindConnectionsAsync` |
| Aggregation / counts | `$match` + `$group` | `KGAnalyticsToolkit`, `CountRelatedEntitiesAsync` et al. |

MongoDB offers `$graphLookup` as its built-in graph traversal primitive. We had to decide **when to use `$graphLookup`**, **when to write a hand-rolled BFS in C#**, and **how to shape `kg.edges` so both approaches perform well**. This ADR documents the framework we arrived at after a gap analysis against the [MongoDB graph database guidance](https://www.mongodb.com/resources/basics/databases/mongodb-graph-database) and the [`$graphLookup` reference](https://www.mongodb.com/docs/manual/reference/operator/aggregation/graphLookup/).

## Decision

**We treat MongoDB as a document database with graph traversal capabilities, not as a graph database.** Every KG query runs against MongoDB; we do not operate a dedicated graph database. Within MongoDB, we split traversal into two code paths and three supporting patterns:

### 1. Two traversal paths

| Path | When to use | Implementation |
| --- | --- | --- |
| **`$graphLookup`** | Direction-pure chains: single relationship label, single direction, depth-aware output | `GetLineageAsync` (masters, successors, parents), single-label reachability |
| **Hand-rolled BFS in C#** | Mixed-direction neighborhood: multi-label traversal where frontier expands via both outgoing AND inbound edges at every hop, with label rewriting for inbound edges | `QueryGraphAsync`, `FindConnectionsAsync` |

The boundary is principled: `$graphLookup` cannot traverse mixed directions in a single invocation — it recurses in one direction, following one `connectFromField → connectToField` mapping. Our "Anakin's network" traversal needs to capture edges like "Ahsoka was Anakin's apprentice" (inbound `apprentice_of`) alongside "Anakin commanded the 501st" (outgoing `commanded`). A hand-rolled BFS that issues two `Find()` queries per hop (outgoing + inbound) with label rewriting in app code correctly expresses this, and with the indexes described below it is fast enough.

We explicitly **do not** try to force `$graphLookup` into mixed-direction traversal by the usual single-direction workarounds (two parallel `$graphLookup`s unioned in post-processing). Those lose the "mixed chain" property — a 2-hop traversal becomes pure-forward-only + pure-reverse-only, not forward-then-reverse-then-forward. A future path exists via a materialized bidirectional view (see Design-007); when that lands, the hand-rolled BFS can be replaced with a single `$graphLookup` against the view.

### 2. Denormalization is the optimization strategy

Every query-relevant field about an edge's endpoints is **denormalized onto the edge document** at ETL time. Writes happen once per Phase 5 ETL run (full delete + insert); reads happen thousands of times per day. The tradeoff is strongly biased toward read performance and filter expressiveness.

Denormalized fields on `kg.edges`:

| Field | Source | Enables |
| --- | --- | --- |
| `fromName`, `fromType` | `kg.nodes.name`, `kg.nodes.type` of `fromId` | Zero-lookup result enrichment |
| `toName`, `toType` | `kg.nodes.name`, `kg.nodes.type` of `toId` | Zero-lookup result enrichment |
| `fromRealm`, `toRealm` | `kg.nodes.realm` of each endpoint | Server-side realm pruning, future `restrictSearchWithMatch` |
| `reverseLabel` | `FieldSemantics.Relationships[label].Reverse` | `kg.edges.bidir` view reverse branch without per-row `$lookup` |
| `fromYear`, `toYear` | Temporal bounds parsed from infobox values OR derived from node lifecycle overlap | Temporal-window `restrictSearchWithMatch`, time-scoped traversal |
| `meta` | `EdgeMeta` (qualifier, rawValue, order) | Rich display, edge-quality filtering |

Denormalization cost: a rebuild of `kg.edges` adds ~12 bytes per edge for realm + ~15 bytes for `reverseLabel`, roughly 16 MB across 600k edges. Negligible compared to the index pages it eliminates from the hot read path.

Denormalization consistency is maintained by the ETL being a **full delete + insert on every Phase 5 run**. There is no in-place update path for edges — the source of truth is the infobox data, and any divergence is resolved by rerunning Phase 5. This rule is what makes denormalization safe; incremental updates would introduce write-time fan-out that would negate the read benefit.

### 3. Single authoritative site for `kg.edges` index creation

`InfoboxGraphService.ProcessAsync` is the **sole** site that creates indexes on `kg.edges`. `RelationshipGraphBuilderService.EnsureIndexesAsync` no longer touches `kg.edges` — it only maintains the LLM-pipeline's private `crawl_state` indexes. This reverses the previous situation where two services created partially-overlapping index sets at different times.

The canonical index set is:

| Name | Keys | Options | Primary read pattern |
| --- | --- | --- | --- |
| `ix_fromId_toId_label` | `(fromId, toId, label)` | unique | Unique constraint + outgoing queries leading with `fromId` (prefix) |
| `ix_fromId_label` | `(fromId, label)` | — | Outgoing with label filter (BFS outgoing pass, forward `$graphLookup`) |
| `ix_toId_label` | `(toId, label)` | — | Inbound with label filter (BFS inbound pass, reverse `$graphLookup`) |
| `ix_label` | `(label)` | — | Label-only scans (`ListRelationshipLabels`, analytics) |
| `ix_continuity` | `(continuity)` | — | Continuity-only scans (`BrowseEdgeLabels`) |
| `ix_sourcePageId` | `(sourcePageId)` | — | LLM extraction path lookups |
| `ix_pairId` | `(pairId)` | sparse | LLM pair cleanup/dedup |

Single-field indexes on `fromId` and `toId` are intentionally omitted — they are covered by `ix_fromId_label` and `ix_toId_label` as leading-key prefixes and were redundant in the previous configuration.

**The unique constraint is load-bearing.** Before this ADR, the ETL produced ~2,700 duplicate `(fromId, toId, label)` tuples because two infobox fields on the same page can both map to the same canonical label against the same target (e.g. both `Masters` and `Teacher` → `apprentice_of` → Obi-Wan). The old unique index creation in `EnsureIndexesAsync` was silently failing because the data already contained duplicates. We fixed the root cause with an ETL dedup step (`DistinctBy((fromId, toId, label))` with quality-ordered preference) so the unique index is now enforceable and the downstream `QueryGraphAsync`'s redundant client-side dedup is removable.

## Rationale

### Why MongoDB over a dedicated graph database

Per MongoDB's own positioning, their platform is suited for workloads that are primarily document-shaped with graph operations as secondary features. That is our situation exactly: the KG is a projection of wiki articles (documents), not a first-class graph workload. We do not need shortest-path-with-weights, centrality, PageRank, or Cypher pattern matching. Our queries are:

- 1-to-N hop reachability
- Shortest path (up to maxHops=4)
- Neighborhood expansion
- Hierarchy walks
- Aggregations

All of these fit inside what `$graphLookup` + hand-rolled BFS + `$match`/`$group` can express cleanly. Running Neo4j or Neptune alongside MongoDB would add operational surface area (second database, ETL sync, second query language in tool descriptions) for marginal gain. We revisit this decision only if the AI agent genuinely needs weighted-path or pattern-matching primitives — so far it does not.

### Why split traversal into two paths rather than unifying on one

Three attempts at unification were considered and rejected:

1. **`$graphLookup` for everything.** Impossible without losing mixed-direction traversal. The single Anakin example above makes it immediately wrong for neighborhood queries.
2. **Hand-rolled BFS for everything.** Works correctly but misses the two things `$graphLookup` does better: depth annotation as a first-class output (free `depthField`) and single-round-trip execution for direction-pure workloads. `get_lineage` returning 3 hops of Jedi masters in one round trip (~48ms tested on dev including view pipeline eval) is a genuine improvement over the same query done as three sequential `Find()`s.
3. **Materialized bidirectional view for everything, then `$graphLookup` over it.** See Design-007 for the prerequisite work that has landed. The view exists on dev and works, but a wholesale rewrite of `QueryGraphAsync` over it needs dedicated validation against the Graph Explorer UI and the AI agent's tool traces. That rewrite is tracked separately, not in this ADR.

The split lets each path play to its strengths without contorting either one.

### Why denormalize instead of computing at query time

Each denormalized field was considered individually against the cost of computing it via `$lookup` or an app-side join:

- **`fromType`, `toType`** were already denormalized before this ADR. The ADR ratifies the pattern rather than introducing it.
- **`fromRealm`, `toRealm`**: the previous implementation did per-edge async `kg.nodes` lookups with a per-request cache (`realmCache` + `PrefetchRealmAsync`). Measured code path was ~35 lines of stateful logic. Replacing with a server-side `$in` clause collapsed the code to 6 lines and made realm filtering eligible for `restrictSearchWithMatch` in any future `$graphLookup` path.
- **`reverseLabel`**: the `kg.edges.bidir` view's reverse branch needs to relabel every edge. Computing this via `$lookup` on `kg.labels` per row costs 600k joins per view evaluation per query — unacceptable. Computing via a hardcoded `$switch` over 100+ label pairs couples the view to `FieldSemantics` and pollutes the pipeline. Denormalization is both correct and cheap.

The pattern is: **if a field is needed for filtering, enrichment, or view transformation on the read path, and can be cheaply computed once at ETL time, denormalize it.** This is consistent with how `GraphNode.temporalFacets` and `GraphNode.startYear`/`endYear` already work.

### Why a single authoritative index-creation site

The previous split between `InfoboxGraphService` (anonymous auto-named indexes on full ETL) and `RelationshipGraphBuilderService.EnsureIndexesAsync` (explicitly-named indexes on service startup) produced parallel near-duplicate indexes with different names. `createIndex` is idempotent only when key spec, name, **and** options all match — mismatched names on identical keys throw `IndexKeySpecsConflict`. This was latent but stable until the unique-constraint failure exposed it.

Rather than harmonize the two sites on a shared helper, we collapse them. `InfoboxGraphService` owns `kg.edges` because Phase 5 is a full delete+insert — it has to re-assert indexes on every rebuild anyway, and any other site attempting to create indexes is either redundant (same spec) or in conflict (different options). `RelationshipGraphBuilderService` retains ownership only of `crawl_state`, which is the LLM pipeline's private working set and has different lifecycle semantics.

## Rules

1. **New KG query code targets `kg.nodes` + `kg.edges`** (or `kg.edges.bidir` where it helps) via either `$graphLookup` for direction-pure single-label walks or hand-rolled BFS for mixed-direction multi-label neighborhoods. Do not introduce a third pattern without updating this ADR.
2. **New denormalized fields on edges are populated in `InfoboxGraphService.ProcessAsync`** during the post-processing loop where `fromType`/`toType`/`toRealm` are already enriched. The source of truth for each denormalized field must be declared in a code comment near the assignment.
3. **`kg.edges` schema changes are not backwards-compatible at the query level.** If a new denormalized field is added, existing data is considered stale and Phase 5 must be rerun to populate it. Query code that filters on the new field must tolerate missing values via `$exists: false` for the window between schema change and ETL rerun (see the realm filter for an example).
4. **`InfoboxGraphService` is the sole owner of `kg.edges` indexes.** Other services must not call `_edges.Indexes.CreateOneAsync` or `CreateManyAsync`. If a new index is needed, add it to the canonical set in `ProcessAsync` and rerun ETL.
5. **Unique constraint `(fromId, toId, label)` is invariant.** If the ETL surfaces duplicates, fix them with upstream dedup logic (as we did) — do not relax the constraint. Duplicate edges are a data quality bug, not a schema feature.
6. **No edge write path that bypasses the ETL dedup step.** The LLM extraction path (Phase 6) must produce edges that respect the unique constraint, or dedupe before insert.
7. **Client-side dedup in query code is smell.** If `QueryGraphAsync` or equivalent calls `DistinctBy((from, to, label))` on results, it means the data has dupes that should be fixed at the ETL. The current dedup in `QueryGraphAsync` around `DistinctBy((e.from, e.to, e.label))` exists for the legacy path where LLM-path edges can exist in forward+reverse pairs with the same label key; it is NOT compensating for ETL bugs.
8. **Temporal filtering is recall-biased by default.** Edges with null temporal bounds pass through a time-window filter unless the caller explicitly opts into strict mode. This matches the reality that most edges don't yet carry explicit temporal bounds.

## Consequences

### Positive

- Two well-understood code paths, each optimal for its workload class.
- Every read-path filter can be pushed server-side (realm, continuity, label, temporal window).
- Indexes are consistent across environments because there is one creation site.
- The unique constraint makes downstream dedup logic removable and catches future ETL bugs early.
- The denormalization pattern is a template for future needs — adding, say, `fromEra`/`toEra` for era-bucketed queries is a 3-line change in the ETL and a filter clause in the query path.
- `$graphLookup` earns its keep in the places it actually shines (`get_lineage`) rather than being forced into every traversal.

### Negative / trade-offs

- **Mixed-direction traversal still uses a hand-rolled BFS with N hops × 2 round-trips.** For `maxDepth=3` on a hub node, that is 6 round-trips plus a final node-enrichment query. This is acceptable at current scale (dev: 595k edges) but will eventually warrant the `kg.edges.bidir`-based rewrite tracked in Design-007.
- **Every Phase 5 rerun rewrites all edges.** Incremental ETL is not supported. For a 600k-edge rebuild this takes minutes on dev; acceptable for a daily sync cadence but not for real-time updates.
- **New denormalized fields require full ETL reruns to populate.** Back-compat `$exists: false` filters handle the gap in production but add filter complexity until the rerun lands. We tolerate this because denormalized fields are added sparingly.
- **`FieldSemantics.Relationships` changes propagate slowly.** Renaming `apprentice_of`'s reverse from `master_of` to something else requires a Phase 5 rerun to refresh `reverseLabel` on existing edges. The `kg.edges.bidir` view picks up new data immediately but reads the stale denormalized column.

### Security / correctness considerations

- Denormalized fields can drift from their source if the ETL is interrupted mid-run. Phase 5 is a single `DeleteManyAsync` followed by `InsertManyAsync`; there is no two-phase commit. An interruption between delete and insert leaves the collection empty until manual recovery. This is an operational concern, not a design one — the ETL is idempotent and rerunning it resolves any state.
- The unique constraint means a Phase 6 LLM-path insert that produces a duplicate of a Phase 5 edge will throw. This is a feature, not a bug — the LLM path should not be writing edges that the deterministic ETL already owns.

## Gaps vs MongoDB best practices

A cross-check against [MongoDB's official data modeling guidance](https://www.mongodb.com/docs/manual/data-modeling/) and [tree structure patterns](https://www.mongodb.com/docs/manual/applications/data-models-tree-structures/) surfaced one genuine gap and three minor ones, captured here so they don't get lost.

### Gap 1 (tracked — implementation starting): Tree-shaped subgraphs are not precomputed

MongoDB's tree-structures guidance recommends precomputed helper fields — **materialized paths**, **ancestor arrays**, **parent references** — for subgraphs that are genuinely tree- or DAG-shaped. This turns "who are X's ancestors?" from an N-hop traversal into a single field read, and "is X a descendant of Y?" into an `$in` check.

Our graph contains several genuinely tree-shaped subsets that today are walked via `GetLineageAsync`:

- **Jedi / Sith master–apprentice lineages** (`apprentice_of`)
- **Biological parent–child chains** (`parent_of`, typically DAG with 2 parents)
- **Government successor chains** (`successor_of` / `predecessor_of`)
- **Planetary containment taxonomy** (the exact labels are TBD until the survey — likely `part_of` / `located_in` / `located_in_system`)

At current scale `GetLineageAsync` is fast enough (48ms for a 5-hop traversal in the dev smoke test), but the guide's pattern offers three concrete wins:

1. **Set membership in O(1)** — "is Luke a descendant of Shmi via `parent_of`?" becomes `{lineages.parent_of_reverse: shmi_id}` on Luke's node, an indexed equality check. No traversal.
2. **Bounded-size helper fields on entities** — unlike neighbor summaries, lineage closures are small and stable: Jedi chains are typically 5–10 deep, family trees 2–10, government successions 5–30, planetary containment 3–5. Well within the "bounded substructure" criterion for embedding.
3. **Removes a tool round-trip for common questions** — the AI agent currently has to call `get_lineage` to answer "who are Luke's ancestors?". With a precomputed array it becomes a one-shot `get_entity_properties` read.

**Implementation plan** (see Design-008):

- Introduce a `HierarchyRegistry` listing `(label, ancestorDirection)` pairs that are known to be tree/DAG-shaped.
- Add a post-processing step in `InfoboxGraphService.ProcessAsync` that walks `kg.edges` for each registered lineage and computes transitive closures keyed by `(pageId, label, direction)`.
- Store closures either as embedded `lineages` on `kg.nodes` (per the guide's "embed small bounded facts" direction) or as a separate `kg.lineages` collection (cleaner separation of concerns). Decision deferred to Design-008 after the label survey.

**Why this is a gap worth fixing, not just a nice-to-have:** the AI agent today burns tool calls walking chains that never change shape until the next ETL run. The denormalized closures match the same cost/benefit calculus that justified denormalizing `fromRealm`/`toRealm`/`reverseLabel` on edges in this ADR — precompute once per ETL, read thousands of times per day, stale-resolution via full rebuild.

### Gap 2 (tracked, low priority): No `(label, fromId, toId)` compound index

We have `ix_fromId_toId_label` (for the unique constraint) and `ix_label` (single-field). A `(label, fromId, toId)` compound — with `label` as the **leading** key — would help queries that scan by label then restrict by source, such as "all `apprentice_of` edges originating from Characters of type Jedi". Currently these use `ix_label` as a seek-range and filter the rest in memory. At current scale the pattern works but wouldn't survive a 10× edge growth.

**Action:** monitor `explain()` plans on any KG analytics query that leads with a label predicate. Add the compound if plan quality degrades.

### Gap 3 (tracked, low priority): No indexed temporal bound search

The temporal window filter in `QueryGraphAsync` and `FindConnectionsAsync` runs as an un-indexed runtime clause combined with seek predicates on `fromId`/`toId`. At current scale this is fine — the leading-key seek narrows to tens or hundreds of edges per hop, and the temporal clause filters them in-memory. But the MongoDB tree-patterns guidance implies that time-scoped traversal on a large graph benefits from a dedicated `(label, fromYear, toYear)` or `(fromYear, toYear)` index when the time window is the primary discriminator.

**Action:** add the index only if a use case emerges that starts with a time window and has no entity anchor (e.g. "all edges active in 19 BBY" with no `fromId` seed).

### Gap 4 (convention, document-only): No `schemaVersion` field

The guide recommends `meta.schemaVersion: 1` on documents to support future migrations. Our full-rebuild ETL makes in-flight multi-version data impossible, so there is no current need. However, the convention is cheap to adopt as a forward-compat marker, and doing so now is easier than retrofitting later if we ever introduce an incremental update path.

**Action:** adopt `schemaVersion` on `GraphNode` and `RelationshipEdge` the next time either model is modified. Do not rebuild just to add it — wait for a change that's happening anyway.

## Alternatives considered

- **Neo4j / Neptune sidecar.** Rejected for the reasons in "Why MongoDB over a dedicated graph database" above.
- **Materialized bidirectional collection** (rather than a view). Rejected for now because the view version exists and performs acceptably (<50ms for 2-hop bidirectional traversal against 1.2M view docs). If view pipeline re-evaluation on every hop becomes a bottleneck under the future `QueryGraphAsync` rewrite, we upgrade to a physical collection rebuilt during Phase 5 — no schema changes needed on the read side because the view shape and physical-collection shape would be identical.
- **Atlas Graph Database features** (Atlas has some graph-specific tooling). Rejected because we run self-hosted MongoDB, not Atlas. If we ever migrate, this is worth revisiting.
- **Relax the unique constraint** to avoid fixing the ETL dedup. Rejected — fixing the ETL is a one-time ~20-line change and guarantees data quality going forward. Relaxing the constraint would bake a latent bug into the schema.
- **Per-query `$lookup` for realm filter** instead of denormalization. Rejected because it is strictly more expensive at every query and the denormalized column is 12 bytes per edge.
- **Auto-generated index names from MongoDB** instead of explicit `ix_` names. Rejected because the conflict-detection story requires stable names: `ix_toId_label` in code must map to a single named index on disk, otherwise `createIndex` becomes unpredictable across environments.

## References

- [MongoDB as a graph database (marketing overview)](https://www.mongodb.com/resources/basics/databases/mongodb-graph-database)
- [`$graphLookup` aggregation stage reference](https://www.mongodb.com/docs/manual/reference/operator/aggregation/graphLookup/)
- ADR-002 (toolkits): [./002-ai-agent-toolkits.md](./002-ai-agent-toolkits.md) — how KG queries are surfaced as AI tools
- Design-001 (temporal facets): [../design/001-temporal-facets.md](../design/001-temporal-facets.md) — node-side temporal model
- Design-002 (edge quality): [../design/002-edge-quality.md](../design/002-edge-quality.md) — upstream noise reduction
- Design-007 (bidirectional edges view): [../design/007-kg-bidirectional-edges-view.md](../design/007-kg-bidirectional-edges-view.md) — the view, `reverseLabel`, planned `QueryGraphAsync` rewrite
- Index definitions: [src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs)
- Query service: [src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs)
- Lineage tool: [src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs](../../src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs) (`GetLineage`)
- Edge model: [src/StarWarsData.Models/KnowledgeGraph/RelationshipEdge.cs](../../src/StarWarsData.Models/KnowledgeGraph/RelationshipEdge.cs)
