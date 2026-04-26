# Design-018: Knowledge Graph Enrichments Architecture

**Status:** Proposal
**Date:** 2026-04-26
**Author:** Patrick Magee + Claude
**Related:** [Design-007 KG Per-Type Node Builders](./007-kg-per-type-builders.md), [Design-013 KG Property/Edge Duality](./013-kg-property-edge-duality.md), [ADR-003 KG Query Architecture](../adr/003-kg-query-architecture.md), [ADR-005 MongoDB Migration Strategy](../adr/005-mongodb-migration-strategy.md)

## Problem

Phase 1 of the KG ETL ([InfoboxGraphService](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs)) is a **delete-and-reinsert** rebuild. Every daily Hangfire run does:

```csharp
await _nodes.DeleteManyAsync(FilterDefinition<GraphNode>.Empty, ct);
await _nodes.InsertManyAsync(nodes, ...);
await _edges.DeleteManyAsync(FilterDefinition<RelationshipEdge>.Empty, ct);
await _edges.InsertManyAsync(filteredEdges, ...);
```

This is intentional and correct for Phase 1: the wiki content drifts, infobox templates evolve, and re-extraction is cheap. The unique constraint on edges (`fromId, toId, label`) plus the per-type builder dispatch keeps the rebuild idempotent.

Phase 2 — agentic enhancement of the KG by an LLM "Holocron" agent — breaks this assumption. Agent-enhanced data lives nowhere stable. If the agent enriches a Character node with a refined birth year, fills in a missing relationship from an article chunk, or writes an evidence-backed description, the next Phase 1 run silently destroys all of it. Re-running the agent on every Phase 1 cycle is impractical: the work is **expensive** (LLM calls), **slow** (rate-limited), and **non-deterministic** (different runs produce slightly different outputs).

We need a separation: Phase 1 owns the canonical infobox-derived view of the KG; Phase 2 owns an additive layer that survives Phase 1 reruns and carries provenance.

## Goals

1. **Phase 1 stays delete-and-reinsert.** No upsert complexity, no merge logic, no special-casing for agent data. The owner contract is unchanged.
2. **Agent work is durable.** Holocron's enrichments survive every Phase 1 rerun. They are never silently overwritten or lost.
3. **Provenance is first-class.** Every enriched property, edge, or facet carries `source` and `evidence`. The AI can distinguish what it's seeing came from the wiki vs from past agent work, and the frontend can surface citations.
4. **Read paths see the merged picture.** Existing consumers swap a single collection name and get the enriched view; their query patterns don't change otherwise.
5. **Stale enrichments are detectable.** When the underlying infobox node changes, enrichments tied to the old content get flagged for re-evaluation, not silently invalidated.
6. **No race conditions between writers.** Phase 1 (`InfoboxGraphService`), Phase 6 (`RelationshipGraphBuilderService`), and Phase 2 (Holocron) operate on disjoint collections — they cannot clobber each other.

## v1 policy: what Holocron will and won't do

The wiki infobox extraction is the **canonical truth foundation**. Holocron polishes around the edges — it adds, it never contradicts. The policy below resolves what would otherwise be open questions about precedence, conflict, and review.

### Permitted (v1)

- **Add** a property when the infobox doesn't have one. Pre-flight: `kg.nodes[pageId].properties[fieldPath]` must be missing or empty.
- **Add** an edge when no edge with the same `(fromId, toId, label)` exists. Pre-flight: must NOT exist in `kg.edges` AND must NOT be Active in `kg.edge_enrichments`.
- **Augment** a list-valued property with new items, deduped against the existing list. Pre-flight: each proposed item must not already appear in `kg.nodes[pageId].properties[fieldPath]` (case-insensitive).
- **FillGap** a null sub-property on an existing entity — the canonical example is filling `kg.edges[(from, to, label)].fromYear` when it's `null`. Pre-flight: the targeted sub-property must currently be `null` in the base collection.

### Forbidden (v1)

- **No Refine.** Holocron never proposes a value when one already exists. The enum doesn't even include the operation. If a future version needs corrections-with-review, it lands as a separate decision and a re-introduced enum value, not a quiet escalation of v1 semantics.
- **No type / name / pageId / wikiUrl mutations.** Those are Phase 1's identity layer.
- **No removal.** Holocron never marks an existing infobox value as "wrong" or hides it. Disagreement gets logged in `kg.events` for human review but does not affect the read view.

### Why this works

If the infobox is wrong, the fix lands via a Wookieepedia edit — that's the source of truth, and it flows back through Phase 1 the next day. Holocron doesn't try to be a second opinion against the wiki; it tries to be the missing context the wiki didn't have room for. Every read consumer can therefore treat infobox values as authoritative and Holocron values as additive context, with no precedence resolution at the merge layer.

### Implications

- The "Temporal precedence" risk in the Risks section disappears — there's no precedence question because conflict is impossible by construction.
- The merged view's `source` flag is binary: a property is either `infobox` (came from `kg.nodes`) or `holocron` (came from a v1-permitted operation). No third category for "agent-corrected".
- Pre-flight checks in `EnhanceNodeAsync` (Stage C) are the load-bearing safety net. The agent prompt teaches the policy, but C# code enforces it before any insert into `kg.enrichments` / `kg.edge_enrichments`.

## Non-goals

- **Full event sourcing.** Nodes are not projections of events; they remain documents. We add an append-only audit log (`kg.events`) for transparency, not for replay.
- **Per-property version history.** Enrichments supersede each other (`status: superseded`); we do not keep a full diff history beyond the event log.
- **Modifying canonical fields.** Holocron does not change `pageId`, `name`, `type`, or `wikiUrl`. Those remain Phase 1's domain.
- **Synchronous read-time LLM calls.** Enrichments are pre-computed and stored. The read path is pure Mongo.
- **Refine operations.** See "v1 policy — forbidden" above. Treated as a separate future decision, not a quiet v2 expansion.

## Architecture overview

```
┌───────────────────────┐              ┌───────────────────────┐
│ InfoboxGraphService   │ writes       │ RelationshipGraph-    │
│   (Phase 1, daily)    ├─────┐        │ BuilderService        │
└───────────────────────┘     │        │   (Phase 6, every 5m) │
                              ▼        └─────┬─────────────────┘
                  ┌───────────────────┐      │ writes
                  │ kg.nodes          │◄─────┘ (edges only)
                  │ kg.edges          │
                  │ kg.labels         │
                  └─────────┬─────────┘
                            │ $lookup (left join)
                            ▼
                  ┌───────────────────┐         ┌──────────────────────┐
                  │ kg.nodes.enriched │◄────────┤ HolocronAgent        │
                  │ kg.edges.enriched │         │   (Phase 2,          │
                  │   (read-only      │         │    background)       │
                  │    Mongo views)   │         └─────┬────────────────┘
                  └─────────┬─────────┘               │ writes
                            │                         ▼
                            │              ┌──────────────────────┐
                            │              │ kg.enrichments       │
                            │              │ kg.edge_enrichments  │
                            │              │ kg.events            │
                            │              └──────────────────────┘
                            ▼
            ┌───────────────────────────────┐
            │ All read consumers            │
            │   (12+ services — see below)  │
            └───────────────────────────────┘
```

The architecture is a **strict separation of writers**:

| Collection | Writer | Pattern | Read consumers |
| --- | --- | --- | --- |
| `kg.nodes`, `kg.edges`, `kg.labels` | `InfoboxGraphService` (Phase 1) | delete-and-reinsert daily | the merged view |
| `kg.edges` (LLM-derived subset) | `RelationshipGraphBuilderService` (Phase 6) | upsert by `sourcePageId` | the merged view |
| `kg.enrichments`, `kg.edge_enrichments` | `HolocronAgent` (Phase 2) | append-only with status transitions | the merged view |
| `kg.events` | `HolocronAgent` (Phase 2) | append-only, immutable | frontend changelog page |
| `kg.nodes.enriched`, `kg.edges.enriched` | — (Mongo views) | computed at read time | every consumer service |

No two writers share a collection. The only contention point — Phase 1's delete-and-reinsert of `kg.edges` overlapping with Phase 6's upserts — is a pre-existing concern with `sourcePageId`-keyed conditional logic and remains unchanged by this design.

## Data model

### `kg.enrichments`

One document per `(pageId, fieldPath)` enhancement.

```js
{
  _id: ObjectId,
  pageId: 12345,                          // links back to kg.nodes._id
  fieldPath: "properties.affiliations",   // dotted path inside kg.nodes
  operation: "Add" | "Augment" | "FillGap",
  // - "Add"     : field didn't exist in infobox; agent created it
  // - "Augment" : field exists as a list; agent appended new items (deduped against existing values)
  // - "FillGap" : field exists but a sub-property is null (e.g. an existing temporal facet
  //               with year=null); agent fills only the null sub-property
  // Refine (changing an existing non-null value) is forbidden in v1 — see "v1 policy" below
  value: <any>,                           // the enrichment payload
  claim: "Anakin Skywalker's birthplace was Tatooine, in the village of Mos Espa.",
  evidence: [
    {
      sourcePageId: 67890,                // citation: another KG page
      chunkId: "chunk_67890_42",          // optional: ArticleChunks reference
      excerpt: "Anakin Skywalker was born on the desert planet of Tatooine...",
      relevanceScore: 0.93
    }
  ],
  llmReasoning: "Three sources agree on Tatooine; ...",
  contentHashAtCreation: "sha256:...",    // hash of kg.nodes[pageId] when proposed
  status: "active" | "superseded" | "stale" | "rejected",
  supersededBy: ObjectId | null,          // points to the newer enrichment
  createdAt: ISODate,
  appliedAt: ISODate,                     // when this became visible in the merged view
  agentVersion: "holocron-v1.0.0",
  modelId: "gpt-5.4-mini",
}
```

Indexes: `(pageId, fieldPath, status)`, `status`, `createdAt`.

**Status lifecycle:**

- `active` — visible in `kg.nodes.enriched`. Newest active enrichment for a `(pageId, fieldPath)` wins.
- `superseded` — replaced by a newer enrichment. Hidden from the view but retained in `kg.enrichments` for the audit trail.
- `stale` — `contentHashAtCreation` no longer matches the current `kg.nodes[pageId].contentHash`. Hidden from the view; flagged for Holocron re-evaluation on the next pass.
- `rejected` — agent or human flagged the enrichment as wrong. Hidden permanently.

### `kg.edge_enrichments`

Same shape as `kg.enrichments` but keyed by edge identity.

```js
{
  _id: ObjectId,
  fromId: 12345,
  toId: 67890,
  label: "apprentice_of",
  operation: "Add" | "FillGap",
  // - "Add"     : agent proposes a NEW edge that doesn't exist in kg.edges
  //              and isn't already an Active enrichment in kg.edge_enrichments
  // - "FillGap" : edge exists in kg.edges but fromYear / toYear is null;
  //              agent fills only the null sub-property
  value: { fromYear?, toYear?, weight?, meta?, ... },
  claim: "...",
  evidence: [...],
  llmReasoning: "...",
  contentHashAtCreation: "sha256:...",
  status: "active" | "superseded" | "stale" | "rejected",
  ...
}
```

Edge enrichments do not violate `kg.edges`' unique constraint because they live in a separate collection. The merged view (`kg.edges.enriched`) handles the join and applies precedence rules.

### `kg.events`

Append-only audit log. One event per state transition (creation, supersession, staleness, rejection).

```js
{
  _id: ObjectId,
  eventType: "EnrichmentCreated" | "EnrichmentSuperseded" | "EnrichmentMarkedStale" | "EnrichmentRejected" | "EdgeEnrichmentCreated" | ...,
  enrichmentId: ObjectId,                 // the kg.enrichments doc this event refers to
  pageId: 12345,                          // for fast filtering
  fieldPath: "properties.affiliations",
  summary: "Holocron added affiliation 'Old Republic' to Yoda based on 3 sources.",
  triggeredBy: "scheduled" | "manual" | "phase1-staleness",
  occurredAt: ISODate,
  agentVersion: "holocron-v1.0.0",
}
```

Indexes: `pageId`, `occurredAt`, `eventType`. This is the collection the frontend changelog page reads from.

## Read path: `kg.nodes.enriched` view

A Mongo view that left-joins `kg.nodes` with active enrichments and merges them into a single document with provenance flags.

```js
db.createView("kg.nodes.enriched", "kg.nodes", [
  {
    $lookup: {
      from: "kg.enrichments",
      let: { pid: "$_id", hash: "$contentHash" },
      pipeline: [
        { $match: {
            $expr: {
              $and: [
                { $eq: ["$pageId", "$$pid"] },
                { $eq: ["$status", "active"] },
                { $eq: ["$contentHashAtCreation", "$$hash"] }
              ]
            }
        }},
        { $sort: { createdAt: -1 }}
      ],
      as: "enrichments"
    }
  },
  // For each enrichment, fold the value into the appropriate fieldPath with a
  // provenance marker. Properties that come from the infobox carry source: "infobox";
  // overlaid enrichments carry source: "holocron" with their evidence and event id.
  { $addFields: { _enrichmentApplied: { $size: "$enrichments" }}}
])
```

The exact `$set` / `$mergeObjects` logic to apply each enrichment by `fieldPath` is moderately complex and best built incrementally as a Mongo migration script (`0009-create-kg-enriched-view.js`).

The shape returned to consumers is the existing `GraphNode` plus:

- Each enriched property reads `{ value: ..., source: "infobox" | "holocron", evidenceIds?: [...], enrichmentId?: ObjectId }`.
- New properties added by Holocron have `source: "holocron"` and don't appear in `kg.nodes.properties` at all.
- The `enrichments` array (active list) is exposed for consumers that want to inspect provenance directly (the AI toolkits, the frontend changelog).

`kg.edges.enriched` is the equivalent view over `kg.edges` ⨝ `kg.edge_enrichments`. It composes with the existing `kg.edges.bidir` view (the bidir view is the bottom layer; enrichment is the top layer).

## Write path: Holocron only writes enrichments

`HolocronAgent` (the Phase 2 background service) is the **sole writer** to `kg.enrichments`, `kg.edge_enrichments`, and `kg.events`. Its workflow:

1. **Pick a node to enhance.** Strategies: round-robin over `kg.nodes`, prioritise nodes with low property count, prioritise high-traffic nodes, react to a stale-enrichment queue.
2. **Gather context.** Read the target node, its 1–2 hop neighbours via `kg.edges.enriched`, and relevant `articleChunks` from the source page (and from cited cross-references).
3. **Propose enhancements.** Single LLM call with toolkit access. The agent emits structured proposals: `{operation, fieldPath, value, claim, evidence}`.
4. **Validate.** Each proposal must cite at least one `evidence[].sourcePageId` or `evidence[].chunkId` that exists. Proposals failing validation are dropped (not written).
5. **Supersede prior enrichments.** If a previous active enrichment exists for the same `(pageId, fieldPath)`, mark it `superseded` and write `EnrichmentSuperseded` to `kg.events`.
6. **Insert new enrichment.** Write `kg.enrichments` document with `status: "active"` and `contentHashAtCreation` set to the current node's hash.
7. **Log.** Write `EnrichmentCreated` to `kg.events`.

`HolocronAgent` **never writes to `kg.nodes` or `kg.edges`**. The merged view does the work at read time.

## Phase 1 contract

To preserve the strict-separation guarantee, `InfoboxGraphService` must:

1. **Never reference `kg.enrichments`, `kg.edge_enrichments`, or `kg.events`** — neither read nor write.
2. **Continue to use delete-and-reinsert.** No upsert logic, no merge.
3. **Set `contentHash` on every node** (already done via [GraphNode.ContentHash](../../src/StarWarsData.Models/KnowledgeGraph/GraphNode.cs)). This is the staleness detector's anchor.
4. **Emit `Phase1RebuildCompleted` to a queue/event** so a separate Holocron staleness pass can run after Phase 1 (see "Staleness handling" below).

## Staleness handling

After every Phase 1 run, a sweep job compares each active enrichment's `contentHashAtCreation` to the current node's `contentHash`:

```python
for enrichment in kg.enrichments.find({status: "active"}):
    node = kg.nodes.find_one({_id: enrichment.pageId})
    if node is None:
        # Page deleted upstream
        enrichment.status = "stale"
        emit("EnrichmentMarkedStale", reason="page_deleted")
    elif node.contentHash != enrichment.contentHashAtCreation:
        enrichment.status = "stale"
        emit("EnrichmentMarkedStale", reason="content_changed")
```

Stale enrichments drop out of the merged view immediately. The Holocron's next scheduled pass picks up stale enrichments preferentially, re-evaluates them with the fresh node context, and either:

- Reasserts the same claim (writes a new active enrichment with the new hash; no user-visible change), or
- Updates the value (new active enrichment supersedes nothing — the old one stays `stale`), or
- Drops it (no new enrichment; user-visible change is the property reverts to the infobox value).

This loop is the durability story: enrichments are never lost, only invalidated and re-evaluated.

## Per-consumer migration plan

12 services read `kg.nodes` and/or `kg.edges` today (see [the consumer matrix](../#kg-consumers) below). Migration is one PR per consumer, in this order:

| Order | Consumer | Risk | What changes |
| --- | --- | --- | --- |
| 1 | [GraphRAGToolkit](../../src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs) | Low | Swap `Collections.KgNodes` → `Collections.KgNodesEnriched`. Update prompt to teach the agent about the `source` field. |
| 2 | [KGAnalyticsToolkit](../../src/StarWarsData.Services/AI/Toolkits/KGAnalyticsToolkit.cs) | Medium | Same swap; **but** decide policy: do enrichments count in aggregations? Default yes; document. |
| 3 | [RelationshipAnalystToolkit](../../src/StarWarsData.Services/AI/Toolkits/RelationshipAnalystToolkit.cs) | Medium | Swap to `kg.edges.enriched`; same prompt update. |
| 4 | [KnowledgeGraphQueryService](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs) | Medium | Swap both. This is the central read API; once it migrates, anything routed through it inherits enrichments. |
| 5 | [TimelineService](../../src/StarWarsData.Services/Timeline/TimelineService.cs) + [KgTimelineBuilderService](../../src/StarWarsData.Services/Timeline/KgTimelineBuilderService.cs) | Medium | Temporal facets are sensitive — apply precedence policy (infobox wins on specific values; Holocron only fills gaps). |
| 6 | [PageDiscoveryExecutor](../../src/StarWarsData.Services/CharacterTimelines/Workflows/PageDiscoveryExecutor.cs) + [CharacterTimelineService](../../src/StarWarsData.Services/CharacterTimelines/CharacterTimelineService.cs) | Low | Read-only graph walk; benefits from enrichments. |
| 7 | [GalaxyMapETLService](../../src/StarWarsData.Services/GalaxyMap/GalaxyMapETLService.cs) + [TerritoryInferenceService](../../src/StarWarsData.Services/GalaxyMap/TerritoryInferenceService.cs) | Medium | Pre-compute consumes enriched data; output `galaxy.years` becomes stale on every enrichment change. Accept daily-lag pattern initially. |
| 8 | [MapService](../../src/StarWarsData.Services/GalaxyMap/MapService.cs) | Low–medium | Frontend galaxy map. User-visible changes — coordinate UI flag for "show agent-enhanced data". |
| 9 | [ChartToolKit](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs) | Low | Reads via the analytics path; inherits #2's choice. |

[InfoboxGraphService](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) and [RelationshipGraphBuilderService](../../src/StarWarsData.Services/KnowledgeGraph/RelationshipGraphBuilderService.cs) **never migrate** — they own the base collections.

Each migration PR is small (a `Collections.X` constant swap plus prompt/policy adjustments). Read-only consumers can be tested in isolation against a seeded enriched fixture.

## Risks and complexities

### High severity

**1. Edge enrichment + 3-writer overlap.** `kg.edges` already has two writers: `InfoboxGraphService` (delete-and-reinsert) and `RelationshipGraphBuilderService` (upsert by `sourcePageId`). Adding Holocron to `kg.edge_enrichments` makes a third concern. The strict separation in this design (Holocron writes a *different collection*, the enriched view joins) sidesteps the race entirely. **No edge writer ever touches another writer's collection.**

**2. ETL invalidation cascade.** `galaxy.years` and `territory.years` are pre-computed snapshots derived from KG state. When Holocron writes an enrichment, those snapshots become stale. Three options, all imperfect:

- *Trigger ETL on every enrichment write.* Chatty; expensive; ETL takes ~minutes.
- *Mark stale + lazy rebuild on read.* Adds read-path complexity.
- *Daily Hangfire rebuild picks up enrichments.* Current pattern; UI lags ~24h behind agent work.

**Decision: accept the daily-lag pattern initially.** Promote to event-triggered rebuild only if users complain. The Holocron's per-day write volume will be moderate; UI freshness within 24h is acceptable for a continuity-database tool.

**3. Provenance leak into AI prompts.** If `GraphRAGToolkit` returns enriched properties without a `source` flag, the AI cites Holocron's own past output as evidence — circular reasoning, no new information. The merged view's shape **must** tag every property `{value, source, evidenceIds?, enrichmentId?}` from day one. Existing toolkit prompts must teach the agent: **infobox is canonical; Holocron enrichments are inference subject to verification**. Adding this later is a breaking prompt change.

### Medium severity

**4. Temporal precedence — resolved by v1 policy below.** Originally framed as a precedence question ("infobox 22 BBY vs Holocron 22 BBY (3:14 ABY) — which wins?"). The v1 policy resolves it categorically: Holocron never proposes a value when one exists, so the conflict cannot occur. See the v1 policy section below.

**5. Index alignment on the enriched view.** Mongo views can use base-collection indexes only when the `$lookup` doesn't disturb the leading-key prefix. `KGAnalyticsToolkit`'s aggregations rely heavily on `ix_temporal_semantic_year`. Need `explain()` after the view is in place. The same risk was present for `kg.edges.bidir` and worked out — strong precedent.

**6. Test fixture pollution.** [ApiFixture](../../src/StarWarsData.Tests/Infrastructure/ApiFixture.cs) seeds `kg.nodes` directly. Tests against the enriched view need fixtures that:

- Seed pure-`kg.nodes` documents (no enrichments) → must round-trip cleanly through the view (left-join semantics).
- Seed `kg.nodes` + matching `kg.enrichments` → exercises the merge logic.
- Seed `kg.nodes` + `kg.enrichments` with mismatched hashes → exercises stale exclusion.

Add a dedicated `EnrichmentFixture` for the third case.

**7. Schema validators don't apply to views.** `kg.nodes` has a `$jsonSchema` validator (ADR-005). The enriched view doesn't, and Mongo can't add one. Consumers that assume strict shape need to tolerate the additional `enrichments` and `_enrichmentApplied` fields. Currently no consumer enforces shape strictly, so this is a watching brief, not a blocker.

**8. Cache invalidation.** `MapService` results are cached in places. Enrichments arriving need cache busting. The merged view document carries an `_enrichmentApplied` count; cache key can incorporate it, or we add a per-node `version: number` field that increments on every enrichment write.

### Lower severity

**9. Orphaned enrichments.** Wookieepedia deletes a page → Phase 1 omits it → enrichments point at a non-existent `pageId`. The staleness sweep handles this (`reason: "page_deleted"`, status → `stale`). Periodically purge `stale` enrichments older than N days; keep the `kg.events` log forever.

**10. Single-writer assumption is gone.** Today, `InfoboxGraphService` is the sole node writer. Adding Holocron means race conditions are *theoretically* possible — Phase 1's `DeleteMany` racing with Holocron's enrichment write. The separate-collection design avoids this entirely (different collections, no shared keys), which is one of the strongest arguments for the architecture as drawn.

## Open questions

These are pending decisions that block detailed implementation but not the high-level shape.

1. **View materialisation strategy.** Pure Mongo view (`createView`) or materialised collection refreshed via `$merge`? Pure view is simpler; materialised is faster reads but needs a refresh trigger. Default to pure view; promote to materialised only if `KGAnalyticsToolkit`'s aggregations get slow.
2. **Holocron cadence.** Hourly? Daily? Burst-when-Phase-1-completes? Likely daily after Phase 1 completes, with an event trigger; details land in the Holocron-specific design doc.
3. **Frontend changelog UX.** Paginated table? Per-node detail panels? Cite-able permalinks? Belongs in a separate frontend design doc.
4. **Agent isolation model.** Is `HolocronAgent` an `AIAgent` registered like `AskAIAgent`, or is it a `BackgroundService` that constructs its own `IChatClient` per pass? Likely the latter — its lifetime model is different from the streaming chat agent. Belongs in Stage A's agents-folder refactor.
5. **Retention policy on `kg.events`.** Forever? 6 months? Cap collection size? Audit trail value vs storage cost. Default forever; revisit if the collection grows unmanageable.

## Not in scope

- Implementation of the Holocron agent itself (separate design doc, Stage B–C of the rollout).
- Frontend changelog page (separate design doc, Stage D).
- Edge enrichment for the LLM-batch `RelationshipGraphBuilderService` — Phase 6's edges are already provenance-tagged via `sourcePageId` and don't need a second enrichment layer.
- User-facing approval workflow for corrections. v1 forbids Refine entirely (see "v1 policy"); a review UI for proposed corrections only matters if and when Refine is reintroduced.
- Automated rollback. If Holocron writes a bad batch of enrichments, manual `kg.enrichments.updateMany({status: "rejected"})` is the answer for now. A "rollback agentVersion X" workflow can come later.

## Migration sequencing

This design lands in stages, each on its own branch:

1. **Stage A** (`feature/agents-folder-refactor`): structural refactor of existing agents into `Services/AI/Agents/`. No behaviour change. Zero risk.
2. **Stage B** (`feature/holocron-skeleton`): new collections (`kg.enrichments`, `kg.edge_enrichments`, `kg.events`), Mongo views, schema validators, Mongo migration scripts, `HolocronAgent` skeleton with a stub `EnhanceNodeAsync`. The view is created but no enrichments exist yet — every consumer that opts in sees the unenriched node.
3. **Stage C** (`feature/holocron-enhancement-logic`): the actual LLM-driven enhancement logic, toolkit, prompt, Hangfire schedule. First real enrichments land in `starwars-dev`.
4. **Stage D** (`feature/holocron-changelog-ui`): API endpoint over `kg.events`, Frontend page, navigation.
5. **Stages E1–E9** (per-consumer migration PRs): one per consumer, in the order tabulated above.

Stages A and B are precondition gates; C–E run in parallel once they land.

## Verification

Each stage carries its own verification checklist. The architecture as a whole is verified when:

- Phase 1 runs end-to-end with active enrichments present, and 0 enrichments are lost.
- The staleness sweep correctly identifies hash-mismatched enrichments after a Phase 1 rerun.
- The merged view's `explain()` shows expected index usage for the AI toolkit's hottest queries.
- Frontend galaxy map renders identically when enrichments collection is empty (parity with current behaviour).
- Frontend changelog page paginates correctly through `kg.events` with citations resolving to live pages.
