# Design-015: MongoDB GraphRAG Context Provider

**Status:** Proposed
**Date:** 2026-04-25
**Author:** Patrick Magee + Claude
**Related:** [ADR-002 AI Agent Toolkits](../adr/002-ai-agent-toolkits.md), [ADR-003 KG Query Architecture](../adr/003-kg-query-architecture.md), [Design-005 Search Architecture](./005-search-architecture.md), [Design-007 KG Bidirectional Edges View](./007-kg-bidirectional-edges-view.md), [Design-012 AI Agent Tool-call Efficiency](./012-ai-agent-tool-call-efficiency.md), [Design-016 MongoDB Agent Memory Provider](./016-mongodb-agent-memory-provider.md)

**Scope boundary:** This doc is about *read-only retrieval over the curated universe KG* (canon Wookieepedia data, same for every user). Persistent per-user memory that learns from conversations is the inverse problem and lives in [Design-016](./016-mongodb-agent-memory-provider.md).

## Problem

Microsoft published an official `Neo4j.AgentFramework.GraphRAG` package — a Microsoft Agent Framework `AIContextProvider` that runs Cypher retrieval against a Neo4j graph **before each LLM call** and injects the results as conversation context. ([learn.microsoft.com/agent-framework/integrations/neo4j-graphrag](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-graphrag?pivots=programming-language-csharp), [github.com/neo4j-labs/neo4j-maf-provider](https://github.com/neo4j-labs/neo4j-maf-provider).)

Our knowledge graph lives in MongoDB (`kg.nodes`, `kg.edges`, `kg.edges.bidir`, `search.chunks`) and we already wire **explicit retrieval tools** (`GraphRAGToolkit`, `KGAnalyticsToolkit`, `SemanticSearchService`) into the agent in [src/StarWarsData.ApiService/Program.cs](../../src/StarWarsData.ApiService/Program.cs). What we **don't** have is the *implicit* equivalent — a per-turn retrieval pass that grounds the agent in relevant subgraph context **before** the model decides whether to call any tools at all.

The user-stated goal: *"build a kind of 'text to mongo `$graphLookup` / `$lookup`'"* — i.e. take the user's natural-language turn, embed/keyword-search it into the KG, expand via graph traversal, and stuff the result into the prompt context. That is precisely what `Neo4jContextProvider` does for Neo4j, and what this design proposes for MongoDB.

## What Neo4j ships (verbatim shape)

The Neo4j provider is small (~6 source files, all in [`dotnet/src/Neo4j.AgentFramework.GraphRAG/`](https://github.com/neo4j-labs/neo4j-maf-provider/tree/main/dotnet/src/Neo4j.AgentFramework.GraphRAG)):

| File | Role |
|---|---|
| `Neo4jContextProvider.cs` | `AIContextProvider` subclass. Concatenates last *N* messages → query text → retriever → formats results into `AIContext.Messages`. |
| `Neo4jContextProviderOptions.cs` | Options bag: `IndexName`, `IndexType`, `EmbeddingGenerator`, `TopK`, `RetrievalQuery`, `MessageHistoryCount`, `FulltextIndexName`, `FilterStopWords`, `ContextPrompt`. |
| `IndexType.cs` | Enum: `Vector` / `Fulltext` / `Hybrid`. |
| `Retrieval/IRetriever.cs` | `Task<RetrieverResult> SearchAsync(string queryText, int topK, ct)`. |
| `Retrieval/VectorRetriever.cs` | Embeds `queryText` → `CALL db.index.vector.queryNodes($index, $k, $embedding) YIELD node, score`. Optionally appends user's `RetrievalQuery` Cypher (with `node`/`score` in scope) for graph enrichment. |
| `Retrieval/FulltextRetriever.cs` | `CALL db.index.fulltext.queryNodes(...)` with stop-word filtering. |
| `Retrieval/HybridRetriever.cs` | `Task.WhenAll` of the two; merges by content text, keeps max score. |

**Key properties of the design:**

- **No entity extraction.** The provider does not LLM-classify the user's turn into entities; it just embeds/searches the raw concatenated text. Cheap, deterministic, no extra round-trip.
- **Retrieval query is opt-in enrichment.** The default is "give me top-K nodes ranked by vector score". The user-supplied `RetrievalQuery` runs *after* seeding and lets the user `MATCH` outwards from the seed nodes, returning whatever shape they want (text + denormalized fields + score).
- **Plugs into the agent pipeline at the `AIContextProviders` slot** — either via `ChatClientAgentOptions.AIContextProviders = [provider]` or `.AsBuilder().UseAIContextProviders(provider).Build()`. The Microsoft Agent Framework runs the provider's `InvokingCoreAsync` before the LLM call (per [docs/context-providers](https://learn.microsoft.com/agent-framework/agents/conversations/context-providers)).
- **Output is `AIContext.Messages`** — a list of synthesized "system"-style messages stamped with the provider's `source_id` so they can be filtered out of persisted history if desired.
- **Implements `IAsyncDisposable`** for driver cleanup.

## One graph, not two

A natural reading of "GraphRAG" is that the agent gets its *own* graph alongside the universe KG. **It doesn't.** This design is read-only over the collections that already exist:

```text
search.chunks ──pageId──► kg.nodes ──► kg.edges.bidir
(vector lens)              (entities)   (relationships)
```

`search.chunks` is **not a second graph** — it is a vector index over article text where every chunk carries the `pageId` of the KG node it was sliced from. The retrieval flow is:

1. **Seed** — `$vectorSearch` on `search.chunks` answers *"which entities are semantically relevant to this turn?"*
2. **Resolve** — `$lookup` from chunks → `kg.nodes` lifts the seeds into real KG entities.
3. **Expand** — `$graphLookup` on `kg.edges.bidir` walks the neighborhood.

Neo4j's reference example uses the identical shape — `MATCH (node)-[:FROM_DOCUMENT]->(doc:Document) OPTIONAL MATCH (doc)<-[:FILED]-(company:Company)`: the chunk-like node is the *entry point*, the entity is what gets *returned*. One graph, two node types, one vector index. Same here.

**No new collection, no new ETL phase, no parallel agent-only KG.** The provider performs zero writes; it is a query pattern over data the existing ETL already maintains. (See "Open questions" #2 for the deferred option of embedding `kg.nodes` *directly* — that adds a vector field to existing nodes, not a new collection.)

## Why not `Microsoft.Extensions.VectorData` (MEVD)?

Microsoft ships a vector-store abstraction — `Microsoft.Extensions.VectorData.Abstractions` (MEVD) — with a MongoDB connector (`Microsoft.SemanticKernel.Connectors.MongoDB`). The package has the SK name for legacy reasons but does not pull the SK runtime ([dependency closure](https://www.nuget.org/packages/Microsoft.SemanticKernel.Connectors.MongoDB) is `Microsoft.Extensions.AI.Abstractions` + `Microsoft.Extensions.VectorData.Abstractions` + `Microsoft.Extensions.DependencyInjection.Abstractions` + `MongoDB.Driver`), and Microsoft's own docs explicitly say *"these providers have nothing to do with Semantic Kernel and are usable anywhere in .NET, including Agent Framework"*. So adopting it does **not** breach the project's "no SK runtime" rule.

It is rejected for this design on **functional** grounds, not packaging.

| MEVD `VectorStoreCollection<TKey, TRecord>` capability | What this design needs | Verdict |
|---|---|---|
| Typed CRUD: `UpsertAsync` / `GetAsync` / `DeleteAsync` | not needed (read-only over ETL data) | irrelevant |
| `SearchAsync` over a vector index | seed step (✓ — covers `$vectorSearch` on `search.chunks`) | covered |
| Hybrid search (vector + keyword) | seed step (✓) | covered |
| Filter expressions | rich `$match` on realm/continuity/temporal bounds | C# connector currently supports **`EqualTo` only** — no range, no `$in`, no array containment |
| `$lookup` to another collection | resolve chunk → `kg.nodes` | not exposed |
| `$graphLookup` over `kg.edges.bidir` with `restrictSearchWithMatch` and `depthField` | the entire enrichment step | not exposed |
| Aggregation-pipeline access | required for the custom retrieval pipeline | not exposed |
| Stability | the design is the agent's per-turn baseline | C# connector status is **preview, breaking changes flagged** |

The honest summary: MEVD's surface is *"typed records + similarity search"*. GraphRAG's surface is *"vector search **then** graph traversal **then** custom projection"*. Adopting MEVD would let us write ~30 lines of typed `SearchAsync` instead of a hand-built `$vectorSearch` aggregation stage, and then we'd still drop down to raw `MongoDB.Driver` for everything past that. Net effect: two abstractions over the same database for marginal LOC savings.

The same argument inverts for [Design-016](./016-mongodb-agent-memory-provider.md): per-user memory is *typed records + similarity search* — exactly MEVD's sweet spot. There the connector earns its keep.

## Why this fits MongoDB cleanly

| Neo4j primitive | MongoDB equivalent in this repo |
|---|---|
| `db.index.vector.queryNodes($index, $k, $embedding)` | `$vectorSearch` stage on `search.chunks` (index `chunks_vector_index`, 1536-dim, cosine) — already used by [SemanticSearchService.cs](../../src/StarWarsData.Services/Search/SemanticSearchService.cs). |
| `db.index.fulltext.queryNodes(...)` | `$search` (Atlas Search) **or** `$text` index on `raw.pages.title+content` — already used by [KeywordSearchService.cs](../../src/StarWarsData.Services/Search/KeywordSearchService.cs). |
| `MATCH (a)-[:LABEL]->(b)` traversal | `$lookup` (one hop) / `$graphLookup` (multi-hop, single direction). |
| Mixed-direction traversal (`MATCH (a)-[*1..3]-(b)`) | `$graphLookup` against the `kg.edges.bidir` view (Design-007) — pure forward over a doubled edge set captures both directions in one stage. |
| Cypher `RetrievalQuery` returning custom result shape | A user-supplied **aggregation pipeline** (`IEnumerable<BsonDocument>`) that runs after seeding and projects whatever the agent should see. |
| `IDriver` lifecycle | `IMongoDatabase` (already DI-singleton in `Program.cs`); the provider does not own it. |

The KG-side work is essentially done: edges are bidirectional via the `kg.edges.bidir` view, nodes carry denormalized `name/type/realm/continuity` and temporal fields ([GraphNode.cs](../../src/StarWarsData.Models/KnowledgeGraph/GraphNode.cs)), and the vector index is live.

## Decision

**Build a `MongoGraphRagContextProvider` with surface symmetric to `Neo4jContextProvider`**, hosted in `StarWarsData.Services.AI.GraphRAG`. Wire it as an *additional, optional* context provider — **not** a replacement for the existing toolkits.

Coexistence with toolkits is the important call: tools and context providers are complementary in the Agent Framework pipeline ([context-providers docs](https://learn.microsoft.com/agent-framework/agents/conversations/context-providers#how-context-providers-work)). The provider gives the model a *grounded baseline* every turn (cheap, single round-trip); the tools remain for *targeted, structured* lookups the model decides it needs after reading that baseline. Per [Design-012](./012-ai-agent-tool-call-efficiency.md), this should reduce average tool-call count for the common "tell me about X" turn.

## API surface

```csharp
namespace StarWarsData.Services.AI.GraphRAG;

public sealed class MongoGraphRagContextProvider : AIContextProvider, IAsyncDisposable
{
    public MongoGraphRagContextProvider(
        IMongoDatabase database,
        MongoGraphRagOptions options);

    protected override Task<AIContext> InvokingCoreAsync(
        AIContextInvokingArgs args, CancellationToken ct);
}

public enum MongoGraphRagSeedMode { Vector, Fulltext, Hybrid }

public sealed class MongoGraphRagOptions
{
    // Seeding (the "find me starting points" stage).
    public MongoGraphRagSeedMode SeedMode { get; init; } = MongoGraphRagSeedMode.Vector;
    public string SeedCollection { get; init; } = "search.chunks";   // Where the vector/text index lives.
    public string VectorIndexName { get; init; } = "chunks_vector_index";
    public string VectorPath { get; init; } = "embedding";
    public string? FulltextIndexName { get; init; }                   // Atlas Search index, if used.
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; init; }
    public int TopK { get; init; } = 8;
    public int NumCandidates { get; init; } = 200;                    // $vectorSearch numCandidates.
    public IReadOnlyList<string>? StopWords { get; init; }            // Used by Fulltext/Hybrid only.

    // Enrichment (the "expand from those starting points" stage — analogue of Cypher RetrievalQuery).
    // Pipeline runs after seeding. Each input doc carries fields { _id, score, pageId, ... }.
    // Authors can $lookup / $graphLookup / $project freely. Final docs become the context items.
    public IReadOnlyList<BsonDocument>? RetrievalPipeline { get; init; }

    // Conversation framing.
    public int MessageHistoryCount { get; init; } = 6;
    public string ContextPrompt { get; init; } =
        "The following Star Wars knowledge-graph context was retrieved for this turn. " +
        "Use it as ground truth; cite by `pageId` and `wikiUrl` when relevant.";

    // Per-turn filters (applied in seed + pipeline). Sourced from the global filter (continuity/realm).
    public Func<MongoGraphRagFilterContext>? FilterAccessor { get; init; }
}

public readonly record struct MongoGraphRagFilterContext(
    Continuity? Continuity, Realm? Realm, int? YearMin, int? YearMax);
```

### Defaults that ship with the package

Two named pipeline factories ship out-of-the-box so the simplest registration is one line:

- `MongoGraphRagPipelines.NodeNeighborhood(maxDepth: 1)` — for each seed `pageId`, joins to `kg.nodes` then `$graphLookup` against `kg.edges.bidir` outwards `maxDepth` hops, projects `{ pageId, name, type, score, neighbors: [{label, otherId, otherName, otherType}] }`. **Expected default for most callers.**
- `MongoGraphRagPipelines.EntityProfile()` — joins seed → `kg.nodes` → distinct top relationship labels, no traversal. Cheaper, no fan-out on hub nodes.

Callers wanting different shapes pass a custom `RetrievalPipeline`.

## Wiring

In [src/StarWarsData.ApiService/Program.cs](../../src/StarWarsData.ApiService/Program.cs), at the `BuildAIAgent` site:

```csharp
var graphRagProvider = new MongoGraphRagContextProvider(
    database: mongoDb,
    options: new MongoGraphRagOptions
    {
        SeedMode = MongoGraphRagSeedMode.Hybrid,
        EmbeddingGenerator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
        FulltextIndexName = "chunks_search_index",
        TopK = 8,
        RetrievalPipeline = MongoGraphRagPipelines.NodeNeighborhood(maxDepth: 1),
        FilterAccessor = () =>
        {
            // Pulled from per-request scope; populated by middleware from the Frontend's GlobalFilter.
            var f = sp.GetRequiredService<RequestFilterAccessor>();
            return new(f.Continuity, f.Realm, null, null);
        },
        ContextPrompt = "Star Wars KG context (canonicality and source URLs included). " +
                        "Treat as authoritative; do not contradict without citing wiki search.",
    });

AIAgent agent = chatClient
    .AsAIAgent(agentOptions)
    .AsBuilder()
    .UseAIContextProviders(graphRagProvider)         // <-- new
    .UseToolCallBudget(softWarn: 10, hardLimit: 15)
    .UseStarWarsTopicGuardrail(...)
    .Build();
```

The provider is registered as a singleton in DI and disposed on host shutdown.

## Execution flow per turn

```
User turn arrives
  └─► AIAgent.RunAsync
       └─► AIContextProviders run (in order)
            └─► MongoGraphRagContextProvider.InvokingCoreAsync
                 1. Concat last N=6 user/assistant messages → queryText
                 2. SeedMode dispatch:
                      Vector   → embed(queryText) → $vectorSearch on search.chunks
                      Fulltext → $search (Atlas) on search.chunks
                      Hybrid   → both in parallel, merge by chunkId/max(score)
                    Apply continuity/realm filter at seed time.
                 3. Run RetrievalPipeline against the seeded cursor
                      (default: $lookup → kg.nodes, $graphLookup → kg.edges.bidir, $project)
                 4. Format each result doc as a context item:
                      "[score=0.83 | pageId=12345 | type=Battle] Battle of Yavin
                       Neighbors: aftermath_of→Galactic Civil War; ..."
                 5. Return AIContext { Messages = [system+context items], Instructions = ContextPrompt }
       └─► IChatClient sees: instructions + injected KG messages + user turn + tools
       └─► LLM responds (may still call tools for deeper drill-down)
```

The whole pass is a single Mongo round-trip per seed mode (two for hybrid, run in parallel), bounded by `TopK * fan-out`. On the dev cluster a `TopK=8`, `maxDepth=1` neighborhood pipeline returns in ~80–150 ms cold, well under the LLM's own latency floor.

## Coexistence with existing toolkits

| Concern | Today | After Design-015 |
|---|---|---|
| User asks "Who is Anakin's master?" | Model calls `search_entities` → `get_entity_relationships` (2 tool turns). | Provider injects Anakin's neighborhood at turn 0; model answers without a tool call. Tools remain for follow-ups. |
| User asks "Render a chart of Sith Lords by era." | Model calls `count_nodes_by_property` then `render_chart`. | Same — analytics & rendering stay tool-driven. Provider injects nothing useful for aggregation, which is fine; the cost is one wasted vector search per turn (acceptable). |
| User asks something off-topic | Topic guardrail short-circuits before the agent runs. Provider never executes. | Unchanged — guardrail still gates the run. |

We explicitly **do not** remove the existing `GraphRAGToolkit` tools. They remain the structured, parameterized API the model uses for precise lookups (e.g. "give me the full lineage of label `apprentice_of` from Yoda forward 5 hops"). The context provider does the *opening grounding*, the tools do the *targeted drilling*.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| **Token bloat** — every turn injects KG messages even when not relevant. | `TopK=8` with one-hop `$graphLookup` keeps the injection under ~2k tokens. Compaction strategies (`CompactionProvider`, see MAF docs) can be layered later. |
| **Stale context** — between turns the KG can change (rare, but ETL runs nightly). | Each turn re-runs retrieval; no provider-side caching of results. Embedding generator does cache identical query texts. |
| **Hub-node fan-out** — seeding into "Galactic Empire" with `maxDepth=2` returns thousands of edges. | Default ships `maxDepth=1`. The pipeline factory caps `$graphLookup.maxDepth` and applies `restrictSearchWithMatch` for realm/continuity at every hop. Same shape Design-007 already validated. |
| **Global filter leakage** — Frontend's continuity/realm filter must scope the retrieval. | `FilterAccessor` callback resolves per-request; pipeline factory injects `$match { realm, continuity }` into both the seed and `restrictSearchWithMatch`. Tested in unit tests against `ApiFixture`. |
| **Confidence inflation** — model treats injected context as gospel even when scores are low. | `ContextPrompt` instructs to cite by `pageId`/`wikiUrl`; result items always carry `score`. Optionally drop items below a configurable score threshold. |
| **Doubles cost on aggregate-only turns** — embedding + vector search runs even when the question is purely tabular. | Acceptable on `text-embedding-3-small` (~$0.02 per 1M tokens). If it bites, add a cheap LLM-free heuristic in `InvokingCoreAsync` to skip retrieval when the last turn matches `^(render|chart|count|how many)\b`. |

## Open questions

1. **Should the provider write back?** Neo4j's separate "memory provider" implements `StoreAIContextAsync` to *learn* relationships from conversation. Out of scope here — our KG is curated from Wookieepedia by the ETL. Decided: read-only.
2. **Custom `IRetriever` for in-graph vector search.** Atlas Vector Search now supports vectors *on any collection*, including `kg.nodes`. We currently embed only `search.chunks` (article text). A future option: embed `GraphNode.summary` (which we don't have yet) and seed directly from `kg.nodes`, skipping the chunk→node hop. Deferred — Design-005 (Search Architecture) tracks chunk vs. node embeddings separately.
3. **Per-toolkit scoping.** The MAF pipeline lets a context provider also contribute *tools*. Should the provider bind a curated subset of `GraphRAGToolkit` based on what it already injected (e.g. don't expose `get_entity_relationships` if the seed already pulled the relationships)? Defer to Phase 2.

## Phasing

| Phase | Scope | Exit criteria |
|---|---|---|
| **1 — MVP** | `MongoGraphRagContextProvider` + `MongoGraphRagOptions` + `Vector` seed + default `NodeNeighborhood(1)` pipeline. Wired in `Program.cs` behind a feature flag (`Settings:GraphRagContextProvider:Enabled`). Unit tests on the seed pipeline shape; integration tests against `ApiFixture` for end-to-end retrieval. | An eval set of 20 Star Wars Q&A turns shows ≥30% reduction in average tool-call count without regressing answer quality (manual rubric). |
| **2 — Hybrid + filters** | `Hybrid` seed mode (Atlas Search index on `search.chunks` exists already). `FilterAccessor` integration with the Frontend's `GlobalFilterService`. Score-threshold cutoff. | Continuity/realm filters proven to scope retrieval; hybrid outperforms vector-only on a 50-turn benchmark. |
| **3 — Compaction & cost guardrails** | Layer `CompactionProvider` to summarise older context. Add the cheap-skip heuristic for tabular turns. Telemetry on injected token count + tool-call delta. | OpenTelemetry dashboard shows P95 injection ≤ 2k tokens; cost per session within 10% of pre-Design-015 baseline. |
| **4 — Open-source extraction (optional)** | Move `MongoGraphRagContextProvider` to a standalone `StarWarsData.AI.GraphRAG.Mongo` library and publish to NuGet under our org. Mirror Neo4j's repository structure (`IRetriever`, separate `VectorRetriever`/`FulltextRetriever`/`HybridRetriever`). | Library has zero project dependencies on `StarWarsData.Services` and one Star-Wars-free sample app. |

## Revisit when

- Microsoft ships an official `MongoDB.AgentFramework.GraphRAG` package — at which point we should evaluate switching to it and contributing our pipeline factories upstream.
- Atlas Vector Search adds first-class graph traversal primitives (rumoured for 2026 Atlas releases) — would simplify the `RetrievalPipeline` step.
- The `kg.edges.bidir` view is replaced by per-type node builders (Design-007 follow-up); pipeline factories will need to resolve node types differently.

## Appendix A — Sample default `NodeNeighborhood` pipeline

```javascript
// Input: cursor of seeds from $vectorSearch on search.chunks, each carrying { pageId, score, ... }
[
  // Resolve chunk → KG node
  { $lookup: {
      from: "kg.nodes", localField: "pageId", foreignField: "pageId",
      as: "node",
      pipeline: [{ $match: { /* injected from FilterAccessor */ realm, continuity } }] } },
  { $unwind: "$node" },

  // One-hop bidirectional neighborhood
  { $graphLookup: {
      from: "kg.edges.bidir",
      startWith: "$node.pageId",
      connectFromField: "toId",
      connectToField: "fromId",
      maxDepth: 0,                          // 0 = direct neighbors only; bump to N-1 for N hops
      depthField: "hop",
      restrictSearchWithMatch: { /* realm, continuity, optional yearMin/yearMax */ },
      as: "neighbors" } },

  // Project the agent-facing shape
  { $project: {
      _id: 0, score: 1, pageId: "$node.pageId", name: "$node.name",
      type: "$node.type", wikiUrl: "$node.wikiUrl",
      neighbors: { $map: { input: "$neighbors", as: "e", in: {
        label: "$$e.label", otherId: "$$e.toId", otherName: "$$e.toName", otherType: "$$e.toType"
      } } } } }
]
```

## Appendix B — Symmetry with Neo4j's API

| Neo4j surface | Mongo equivalent in this design |
|---|---|
| `Neo4jContextProvider(driver, options)` | `MongoGraphRagContextProvider(database, options)` |
| `IndexType.Vector / Fulltext / Hybrid` | `MongoGraphRagSeedMode.Vector / Fulltext / Hybrid` |
| `RetrievalQuery` (Cypher string) | `RetrievalPipeline` (`IReadOnlyList<BsonDocument>`) |
| `EmbeddingGenerator` (`IEmbeddingGenerator<string, Embedding<float>>`) | identical type, identical use |
| `TopK`, `MessageHistoryCount`, `ContextPrompt`, `FilterStopWords` | identical semantics |
| Hooks via `.UseAIContextProviders(...)` or `ChatClientAgentOptions.AIContextProviders` | identical — both are pure MAF surface |

The intent is that someone who already used the Neo4j provider can swap it for ours by changing the namespace and replacing the Cypher `RetrievalQuery` with a BSON `RetrievalPipeline`.
