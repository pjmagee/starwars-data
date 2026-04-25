# Design-016: MongoDB Agent Memory Provider

**Status:** Proposed
**Date:** 2026-04-25
**Author:** Patrick Magee + Claude
**Related:** [Design-003 AI Game Master](./003-ai-game-master.md), [Design-015 MongoDB GraphRAG Context Provider](./015-mongodb-graphrag-context-provider.md), [ADR-001 Internal API Auth](../adr/001-internal-api-auth.md), [CLAUDE.md (GDPR section)](../../CLAUDE.md)

**Scope boundary:** This doc is about *persistent, per-user, write-back memory* the agent **learns from conversations**. Read-only retrieval over the curated universe KG (canon Wookieepedia data, identical for every user) is the inverse problem and lives in [Design-015](./015-mongodb-graphrag-context-provider.md).

## Problem

Microsoft's `neo4j-agent-memory` package ([learn.microsoft.com/agent-framework/integrations/neo4j-memory](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory?pivots=programming-language-csharp), [github.com/neo4j-labs/agent-memory](https://github.com/neo4j-labs/agent-memory)) gives Agent Framework agents a persistent, growing memory backed by a knowledge graph. Each turn, the provider runs *both* sides of the `AIContextProvider` lifecycle:

- `ProvideAIContextAsync` — embed the user's incoming message, recall semantically related entities/facts/preferences, inject as context messages.
- `StoreAIContextAsync` — extract entities and facts from the assistant's response (and the user's turn), persist them, link them to existing memory nodes.

The Neo4j package taxonomy lists three memory tiers:

| Tier | What it stores | Lifetime |
|---|---|---|
| Short-term | Per-session conversation history | Conversation/session |
| Long-term | Entities, preferences, facts extracted from interactions | Persistent across sessions, scoped per user |
| Reasoning | Past reasoning traces and tool-usage patterns | Persistent, optional |

**The C# package does not exist.** The MS Learn page says verbatim *"This provider is not yet available for C#. See the Python tab for usage examples."* Adopting this pattern means writing the C# equivalent ourselves on top of MongoDB.

## Why this matters here

Two motivations, in declining priority:

1. **AI Game Master campaign continuity.** [Design-003](./003-ai-game-master.md) describes a Star Wars 5e GM running multi-session campaigns. A campaign needs persistent state — *which NPCs has the GM introduced, what side quests are open, what era is the campaign set in, who killed who in session 3*. Today Design-003 sketches a `game.*` namespace for highly-structured RPG state (character sheets, HP, dice rolls, quest log). What it lacks is the *unstructured, learned* layer — narrative beats, GM rulings, NPC personalities the GM invented and must stay consistent about three sessions later. That layer is exactly what an agent memory provider gives.

2. **Per-user preferences for the universe Q&A agent.** Users tell the agent things in passing: *"I prefer Legends continuity"*, *"Don't show me Sith content, my kid uses this account"*, *"I'm researching the Old Republic for a campaign"*. We currently lose all of this between sessions. The Frontend's `GlobalFilterService` covers explicit continuity/realm toggles, but conversational preferences (topic biases, ongoing context, in-progress research) need somewhere to live.

Neither motivation is well served by the universe KG. Mixing learned/per-user state into `kg.nodes` would corrupt the canon for every other user and is a privacy/GDPR landmine.

## Two graphs, intentionally

Per [Design-015's "One graph, not two"](./015-mongodb-graphrag-context-provider.md#one-graph-not-two) we explicitly avoided forking the universe KG. **This design adds a *second*, genuinely separate graph — and that separation is the point.**

| | Universe KG (`kg.*`) | Agent Memory (`memory.*`) |
|---|---|---|
| Source | Wookieepedia ETL | User conversations |
| Truth status | Canonical, curated, citable | Learned, mutable, fuzzy |
| Scope | Same for all users | Per user (Keycloak `sub`) |
| Lifecycle | Versioned with releases, append-mostly | Mutates every turn; entries decay |
| Access pattern | Read-only at runtime | Read + write at runtime |
| GDPR | n/a (no PII) | Per-user delete/export required |
| Trust | Cite by `pageId` / `wikiUrl` | Cite cautiously; LLM-extracted may hallucinate |

Cross-references between the two graphs are unidirectional: `memory.entities` may carry a `kgPageId` field linking a learned entity ("the user keeps mentioning *Dash Rendar*") back to the canonical KG node. The reverse (canon KG referring to memory) never happens.

## Data model

All collections under a new `memory.*` namespace, namespaced via `Settings.Collections.*` constants. Every document carries a `userId` (Keycloak `sub`) and is indexed on it. GDPR delete = `delete_many({ userId })` per collection.

| Collection | Purpose | Indexed on | Vector field |
|---|---|---|---|
| `memory.entities` | LLM-extracted entities the user has talked about | `{ userId: 1, name: 1 }`, `{ userId: 1, kgPageId: 1 }` | `nameEmbedding` (1536-dim) |
| `memory.facts` | Declarative facts about the user or their world ("user prefers Legends", "playing a Twi'lek scoundrel") | `{ userId: 1 }` | `factEmbedding` (1536-dim) |
| `memory.relations` | Edges between memory entities (analogue of `kg.edges`, but per-user and learned) | `{ userId: 1, fromId: 1 }`, `{ userId: 1, toId: 1 }` | none |
| `memory.traces` | (Optional, deferred) reasoning traces / tool-call patterns for self-improvement | `{ userId: 1, ts: -1 }` | none |

Short-term memory (conversation history) is **not** in this namespace — the `chat.sessions` collection (already exists per `Settings.cs`) keeps the running message log. Memory is the *long-term* layer that distills facts out of those sessions.

### `memory.entities` shape

```csharp
public sealed record MemoryEntity
{
    [VectorStoreKey] public required string Id { get; init; }            // ULID
    [VectorStoreData(IsIndexed = true)] public required string UserId { get; init; }
    [VectorStoreData(IsIndexed = true)] public required string Name { get; init; }
    [VectorStoreData(IsIndexed = true)] public required string Type { get; init; }   // person|place|faction|...
    [VectorStoreData] public string? KgPageId { get; init; }            // nullable link to canon KG
    [VectorStoreData] public string? Description { get; init; }
    [VectorStoreData] public required DateTime FirstSeenUtc { get; init; }
    [VectorStoreData] public required DateTime LastSeenUtc { get; init; }
    [VectorStoreData] public int MentionCount { get; init; }
    [VectorStoreData] public required string SourceSessionId { get; init; }

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float>? NameEmbedding { get; init; }
}
```

The annotations are MEVD's; see [Define your data model](https://learn.microsoft.com/dotnet/ai/vector-stores/define-your-data-model). Persistence is via `VectorStoreCollection<string, MemoryEntity>` from `Microsoft.SemanticKernel.Connectors.MongoDB` — explicitly sanctioned by the carve-out in our [no-SK-runtime feedback rule](../../CLAUDE.md) (the package's dependency closure does not include any SK runtime/Kernel/Plugin/Function packages; see [Design-015 § Why not MEVD?](./015-mongodb-graphrag-context-provider.md#why-not-microsoftextensionsvectordata-mevd)).

`memory.facts` and `memory.relations` follow the same MEVD-typed shape.

## Why MEVD here (and not for Design-015)

Design-015 rejected MEVD because the read path is *vector search → `$lookup` → `$graphLookup` → custom projection*, and MEVD's surface stops at `SearchAsync`. None of those graph stages are exposed.

The memory write path is the inverse: **typed records, upserted/searched/deleted by primary key + vector similarity**, no graph traversal. That is precisely what `VectorStoreCollection<TKey, TRecord>` is for. Concretely, MEVD removes the following hand-rolled work:

- BSON serialization of `MemoryEntity` (MEVD wires `MongoDB.Bson.Serialization`, honours `[BsonElement]`)
- Vector index creation (`AddMongoVectorStore` extension creates the search index from the `[VectorStoreVector]` attribute)
- Embedding generation on upsert (MEVD calls the registered `IEmbeddingGenerator` automatically when a string-typed field is mapped to a vector property)
- Filter expressions for "find entities for `userId = X` similar to query text Y" (MEVD's `EqualTo` is sufficient here — userId is an exact match, the rest is vector similarity)

Net: ~150 lines of plumbing avoided, with the trade-off that the C# MongoDB connector is still preview. Acceptable risk because (a) memory is feature-flagged behind Design-003 / per-user opt-in, not core path, and (b) the data model is small enough that a connector swap (or fallback to raw `MongoDB.Driver`) is contained.

## Provider shape

```csharp
namespace StarWarsData.Services.AI.Memory;

public sealed class MongoMemoryContextProvider : AIContextProvider, IAsyncDisposable
{
    public MongoMemoryContextProvider(
        IUserIdAccessor userId,                                          // resolves Keycloak sub from request scope
        VectorStoreCollection<string, MemoryEntity> entities,
        VectorStoreCollection<string, MemoryFact> facts,
        IChatClient extractionClient,                                    // small/cheap model for entity extraction
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        MongoMemoryOptions options);

    // Read path — runs before each LLM call.
    protected override Task<AIContext> InvokingCoreAsync(
        AIContextInvokingArgs args, CancellationToken ct);

    // Write path — runs after each LLM call.
    protected override Task InvokedCoreAsync(
        AIContextInvokedArgs args, CancellationToken ct);

    // GDPR support.
    public Task DeleteAllForUserAsync(string userId, CancellationToken ct);
    public IAsyncEnumerable<MemoryExportEntry> ExportForUserAsync(string userId, CancellationToken ct);
}

public sealed class MongoMemoryOptions
{
    public int RecallTopK { get; init; } = 6;
    public int RecallMessageHistoryCount { get; init; } = 4;             // turns of context that drive recall
    public bool ExtractEntities { get; init; } = true;
    public bool ExtractFacts { get; init; } = true;
    public bool ExtractRelations { get; init; } = false;                 // Phase 3
    public string ExtractionModel { get; init; } = "gpt-4o-mini";
    public TimeSpan EntityStaleAfter { get; init; } = TimeSpan.FromDays(180);
    public string ContextPrompt { get; init; } =
        "The following memory was recalled for this user. Treat preferences and " +
        "facts as authoritative for *this user only*; do not generalize across users.";
}
```

### Per-turn flow

```text
User turn arrives
  └─► AIAgent.RunAsync
       └─► AIContextProviders run
            └─► MongoMemoryContextProvider.InvokingCoreAsync       (READ)
                 1. Resolve userId from IUserIdAccessor (Keycloak sub).
                 2. Concat last N=4 messages → recallText.
                 3. Embed recallText → vector.
                 4. entities.SearchAsync(vector, top=K, filter: UserId == userId).
                 5. facts.SearchAsync(vector,    top=K, filter: UserId == userId).
                 6. Format both lists into AIContext.Messages.
                 7. Return { Messages = [...], Instructions = ContextPrompt }.
       └─► IChatClient → LLM
       └─► Response back through pipeline
       └─► MongoMemoryContextProvider.InvokedCoreAsync             (WRITE)
                 1. Run extraction prompt against gpt-4o-mini:
                      input  = (user turn + assistant response)
                      output = { entities: [...], facts: [...], relations: [...] }
                    Bounded JSON schema; structured outputs.
                 2. For each extracted entity: upsert by (userId, name).
                    On hit, increment mentionCount + bump lastSeenUtc.
                    On miss, embed name + insert.
                 3. For each fact: dedupe by (userId, predicate, object) similarity ≥ 0.92.
                 4. (Phase 3) Resolve canon KG match by embedding name → kg.nodes vector
                    search; if cosine ≥ 0.85 set kgPageId.
```

### Plug-in to the agent

```csharp
AIAgent agent = chatClient
    .AsAIAgent(agentOptions)
    .AsBuilder()
    .UseAIContextProviders(
        graphRagProvider,        // Design-015: read universe KG
        memoryProvider)          // Design-016: read+write per-user memory
    .UseToolCallBudget(...)
    .UseStarWarsTopicGuardrail(...)
    .Build();
```

The two providers compose cleanly. GraphRAG's recalled universe context and Memory's recalled user context land in the same prompt as separate, source-stamped message blocks. Order matters: GraphRAG first (canon ground truth), Memory second (user-specific overlay).

## GDPR alignment

CLAUDE.md mandates "Delete All My Data" and "Export My Data" in the Profile page. The current implementation deletes records across `chat.*` and a few feature collections; this design adds:

- **Delete:** `MongoMemoryContextProvider.DeleteAllForUserAsync(userId)` issues `delete_many({ userId })` against `memory.entities`, `memory.facts`, `memory.relations`, `memory.traces`. Atomic per-collection; no cross-doc invariants to maintain.
- **Export:** `ExportForUserAsync(userId)` aggregates the four collections into a JSON document the user can download. Vector embeddings are excluded from the export (they're derived data and unhelpful to a human).

The `userId` index on every collection makes both operations cheap regardless of corpus size. New collection deletions get added to the existing `Profile` page wiring.

## AI Game Master interaction (Design-003)

Design-003 already proposes a structured `game.*` namespace for character sheets, HP, dice rolls, and quest log — the *crunchy* RPG state. This design supplies the *narrative* layer:

| Concern | Lives in |
|---|---|
| HP, AC, levels, ability scores, dice rolls | `game.characters`, `game.combat` (Design-003) |
| Quest log entries (structured: title, status, objectives) | `game.sessions.questLog[]` (Design-003) |
| Story flags (`empire_alerted: true`) | `game.sessions.flags` (Design-003) |
| GM-invented NPC personalities, quirks, voice | `memory.entities` (this design) |
| Player's stated preferences ("I want a noir tone", "no Force-sensitive enemies for now") | `memory.facts` (this design) |
| "Three sessions ago I told the bartender Naal that we were Bothans" | `memory.facts` + `memory.relations` (this design) |

The two layers are queried together by the GM agent: structured state from `game.*` for rules-bound decisions, learned memory from `memory.*` for narrative consistency.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| **Cost — extra LLM call per turn for extraction.** | Extraction model defaults to `gpt-4o-mini`; structured-output JSON schema keeps tokens tight. Skip extraction when the response was a tool-only turn (no narrative content). Async queue option: extraction happens off the critical path with eventual consistency. |
| **Hallucinated memory pollution.** | Extraction prompt includes few-shot examples and a hard *"if uncertain, return empty"* instruction. Confidence score on each extracted item; below threshold → discarded. Manual `/memory/review` UI for the user to inspect and prune (Phase 4). |
| **Entity-name drift** (LLM extracts "Dash" once and "Dash Rendar" next time). | Upsert by `(userId, vector_similarity(name, existing.name) ≥ 0.9)` not by literal `name` equality. Coalesces variants under the canonical first-seen name. |
| **GDPR leak via embeddings.** | Embeddings derived from user content are PII. Treat `memory.*` collections as PII-equivalent; included in delete; excluded from analytics; not transferred to dev DB. |
| **Cross-user leakage.** | Every read filter and every write key is `(userId, ...)`. Provider asserts `IUserIdAccessor` returned a non-null `sub`; missing user = no recall, no extraction. Integration test fixture covers this. |
| **Memory bloat over time.** | TTL-style sweep: entries with `lastSeenUtc` older than `EntityStaleAfter` (180d default) and `mentionCount = 1` are archived. Hangfire daily job. User can disable. |
| **Extraction lag-induced inconsistency.** | If extraction is async, recall in turn N+1 may miss what N said. Acceptable for narrative use cases (next-session-onwards consistency). For tight loops (combat) the GM agent uses `game.*` structured state, not memory. |
| **MEVD MongoDB connector is preview.** | Locked to a known version; smoke tests on upgrade. Fallback contingency: drop to raw `MongoDB.Driver` (~150 LOC of typed plumbing); the data model is small enough that this is a 1-day rewrite. |

## Open questions

1. **Session-scoped vs cross-session memory.** Should facts learned in a Star Wars Q&A chat surface in an AI GM campaign for the same user? Default: cross-session (one user = one memory graph). Per-app scoping deferred — would add an `appId` field, easy retrofit.
2. **User-visible memory inspection.** A `/profile/memory` page that lists what the agent thinks it knows, with delete buttons per entry. Strong privacy/UX win; Phase 4.
3. **Export format.** Plain JSON for Phase 1; consider [the Personal Information Format spec](https://w3c.github.io/dpv/) once we have actual user feedback.
4. **Multi-tenant memory across users in a campaign.** Could a co-op AI GM campaign share memory across the players at one table? Design-003 today is single-player. Defer.
5. **Should `memory.entities` share the `kg.nodes` `pageId` namespace?** Tempting (cross-graph joins), but breaks per-user privacy if a malicious query exfiltrates by `pageId`. Decision: separate namespaces, optional `kgPageId` link only.

## Phasing

| Phase | Scope | Exit criteria |
|---|---|---|
| **1 — Facts only** | `memory.facts` + provider read/write paths + GDPR delete/export. No entity extraction yet — only explicit declaratives ("user said X about themselves"). MEVD wiring in DI. Feature-flagged off by default; opt-in per user. | Profile page shows "Memory: enabled" toggle; opting in then asking *"remember I prefer Legends"* shows up in next-session recall. Delete + export round-trip verified in integration tests. |
| **2 — Entity extraction** | Add `memory.entities`. Run extraction on every turn (when enabled). Vector recall by name similarity. Confidence-thresholded upsert. | Eval set of 30 multi-turn conversations: ≥80% of explicitly mentioned proper nouns recalled in a follow-up turn 5 messages later. False-positive rate (entity hallucinated from nothing) ≤ 5%. |
| **3 — Relations + KG cross-link** | Add `memory.relations`. Phase 3 of extraction emits edges. Resolve `kgPageId` via cosine ≥ 0.85 against `kg.nodes`. | Sample query *"who did I introduce to Han Solo?"* returns a learned-relation answer with both ends linked back to the canon KG. |
| **4 — User-facing controls + traces** | `/profile/memory` UI. Per-entry delete. Optional `memory.traces` for tool-call self-improvement. AI GM (Design-003) consumes `memory.entities` for NPC continuity. | Design-003 GM agent runs three sessions of the same campaign without contradicting itself on a previously-introduced NPC's faction or appearance. |

## Revisit when

- Microsoft ships a C# `agent-memory` package. If it lands as a clean MEVD consumer, switch and contribute our extraction pipeline upstream.
- Atlas Vector Search adds first-class TTL on vector indexes. Would simplify the staleness sweep.
- We adopt structured outputs / function calling for extraction at the model level rather than via JSON-mode prompting. Cleaner and cheaper.
- The user-facing privacy posture changes (e.g. enterprise tenant) — may require encryption-at-rest of memory fields, key rotation, etc.

## Appendix A — Extraction prompt sketch

```text
SYSTEM
You extract structured memory from a user/assistant exchange about Star Wars.
Output JSON matching the schema. Only extract things that are CLEARLY stated
in the messages — do not infer, summarize, or generalize. If nothing is
extractable, return empty arrays.

SCHEMA
{
  "entities": [
    { "name": string, "type": "person|place|faction|ship|item|other",
      "description": string|null, "confidence": 0.0..1.0 }
  ],
  "facts": [
    { "subject": "user"|"world"|<entityName>,
      "predicate": string, "object": string, "confidence": 0.0..1.0 }
  ],
  "relations": [
    { "fromName": string, "label": string, "toName": string,
      "confidence": 0.0..1.0 }
  ]
}

USER MESSAGE
<verbatim user turn>

ASSISTANT MESSAGE
<verbatim assistant turn>
```

## Appendix B — Symmetry with Neo4j's memory provider

| Neo4j Python (`neo4j-agent-memory`) surface | This design's MongoDB equivalent |
|---|---|
| `MemoryClient(MemorySettings)` | DI-registered `IMongoDatabase` + MEVD `VectorStore` |
| `Neo4jMicrosoftMemory.from_memory_client(memory_client, session_id=...)` | `MongoMemoryContextProvider` constructed per request scope; `userId` from Keycloak |
| `create_memory_tools(memory)` | Optional Phase 4: explicit `memory.search` / `memory.forget` tools added alongside the implicit context-provider path |
| Three-tier (short-term / long-term / reasoning) | Short-term lives in `chat.sessions` (already shipped); long-term in `memory.{entities,facts,relations}`; reasoning in `memory.traces` (Phase 4) |
| Multi-stage extraction pipeline | Single-shot extraction prompt against `gpt-4o-mini`; multi-stage deferred unless quality demands it |
| `context_providers=[memory.context_provider]` | `.UseAIContextProviders(memoryProvider)` — identical hook; `AIContextProvider` is the cross-runtime base class |
