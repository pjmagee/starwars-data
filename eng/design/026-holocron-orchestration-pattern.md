# Design-026: Holocron orchestration pattern — picking the right Microsoft Agent Framework shape

**Status:** Proposal
**Date:** 2026-04-29
**Author:** Patrick Magee + Claude
**Related:** [Design-018 KG enrichments architecture](./018-kg-enrichments-architecture.md), [Design-020 Holocron async pipeline](./020-holocron-async-pipeline.md), [Design-025 Holocron tool-using agent](./025-holocron-tool-using-agent.md), [ADR-006 Long-running AI workflow pipelines](../adr/006-long-running-ai-workflow-pipelines.md)

## TL;DR

Design-025 commits Holocron to a tool-using-agent architecture but leaves an implementation gap: *which* Microsoft Agent Framework orchestration shape do we use? The framework offers five named orchestrations — Sequential, Concurrent, Handoff, Group Chat, Magentic — plus the lower-level building blocks (Workflow, Sub-Workflow, AIAgent, AIAgentHostExecutor, agent-as-tool).

This doc evaluates each against Holocron's actual workload shape and recommends:

> **A single tool-using `AIAgent` (the Holocron extractor), wrapped in `AIAgentHostExecutor`, fanned out per chunk-batch via a concurrent `WorkflowBuilder` graph in the same workflow that already runs Discovery → Bundling → Apply.**

In framework taxonomy: **a custom workflow with concurrent fan-out of agent-host executors**, not one of the five named multi-agent orchestrations. The five named patterns are *multi-agent* orchestrations — coordinating two or more *different* agents. Holocron has one agent that runs many times in parallel over different chunk-batches; that's a fan-out/fan-in topology, not a multi-agent collaboration. Confusing the two would lead to over-engineering.

What we explicitly do **not** use:

| Pattern | Why not |
|---|---|
| Sequential orchestration | We don't have multiple specialised agents that need to run in a defined order; we have one agent that runs many times. |
| Group Chat orchestration | No debate or consensus-building — the agent's tool calls are deterministic encodings, not conversational viewpoints. |
| Handoff orchestration | No specialist-domain delegation — one agent handles all encoding because the tools enforce the rules. Handoff adds mesh complexity and a `HandoffAgentExecutor` for marginal gain. |
| Magentic orchestration | **Not supported in C# in Microsoft.Agents.AI 1.1.0** (Python-only as of writing). Also overkill: Magentic is for open-ended problems with no predetermined solution path; Holocron's task is well-bounded ("for each fact, encode it"). |
| Sub-workflow per batch | Each nesting level adds a superstep loop and serialization overhead. The flat-workflow + fan-out alternative gets the same isolation with less indirection, since per-executor checkpoints already cover per-batch state. |
| Agent-as-tool (one agent calls another via a tool) | The encoding tools are deterministic Mongo lookups, not LLM invocations. There is no LLM sub-task the extractor needs to delegate. |

## Patterns considered

### The five named multi-agent orchestrations (framework built-ins)

Source: [Workflow orchestrations](https://learn.microsoft.com/agent-framework/workflows/orchestrations/), the per-pattern docs, and the comparison table in [AI agent orchestration patterns](https://learn.microsoft.com/azure/architecture/ai-ml/guide/ai-agent-design-patterns).

**Sequential** — Agents execute one after another in a defined order, each agent reads the previous agent's output. Best for step-by-step refinement with clear stage dependencies (e.g. "draft → review → publish"). Holocron's stages (Discovery → Bundling → Extraction → Apply) are sequential, but only Extraction is an LLM. The other stages are deterministic executors. Wrapping Extraction in a Sequential *orchestration* would only help if there were a second LLM stage — there isn't.

**Concurrent** — Agents execute in parallel on the same task; results are aggregated. Best for independent analysis from multiple perspectives ("research + analyse + draft", all reading the same input). Holocron has many *batches*, not many *perspectives* — the agent does the same thing on each batch. So Concurrent **at the orchestration level** doesn't fit (there's only one agent), but Concurrent **at the workflow-graph level** (fanning out the same agent over many batches) is exactly what we want. Subtle distinction worth being precise about.

**Handoff** — Agents transfer control to one another based on context, in a mesh topology. Best when the right specialist emerges during processing (customer support routing, incident triage). Internally uses a specialised `HandoffAgentExecutor` that injects handoff tools. Holocron has no specialists to hand off to — the tool layer enforces the rules; the LLM only thinks in language and calls tools. Adopting Handoff would mean inventing fake specialists ("entity-extractor", "label-encoder") that the same single LLM-with-tools handles trivially.

**Group Chat** — Agents collaborate in a shared conversation, broadcast-after-each-turn, with a manager picking the next speaker. Best for iterative refinement, multi-perspective analysis, maker-checker QA. Holocron has no "perspectives" to balance — encoding decisions aren't subjective; they're tool-call results. A Group Chat would just have one agent talking to itself.

**Magentic** — A manager agent dynamically plans, selects specialists, and adapts via a task ledger. Best for open-ended problems with no predetermined plan ("research and write a comparative report"). Holocron's task is the opposite — it's well-bounded ("walk this batch's chunks; for each fact, encode it"). Plus: **Magentic isn't supported in C# yet** ([docs](https://learn.microsoft.com/agent-framework/workflows/orchestrations/magentic) say "Magentic Orchestration is not yet supported in C#" as of `Microsoft.Agents.AI` 1.1.0). Implementing Magentic-like dynamic planning by hand adds substantial complexity for a task that doesn't need it.

### The lower-level building blocks

**Workflow + Executor + Edges** — The graph-based primitive. Custom executors connected by edges. The current Holocron pipeline is built on this. We keep this layer.

**AIAgent + AIAgentHostExecutor** — How any `AIAgent` (a tool-using LLM in our case) gets wrapped to participate in a workflow. The host executor handles message caching, turn-token signalling, streaming events, downstream dispatch, and checkpointing of agent session state. **This is the load-bearing piece for Design-025's vision.** Tool-using agents in workflows are first-class.

**Sub-workflows** — A workflow can be wrapped as an executor and embedded in a larger workflow. Each nesting level runs its own superstep loop. Useful when: (a) you want to reuse a self-contained workflow as a step elsewhere, or (b) the inner workflow has its own checkpointing concerns separate from the outer. **Adds overhead** — the docs explicitly warn "keep nesting depth reasonable for performance-sensitive scenarios." For Holocron, per-executor state already checkpoints per-batch progress; wrapping each batch in its own sub-workflow buys no isolation we don't already have.

**Agent-as-tool** — A whole agent exposed to another agent as a callable tool. Useful when an LLM stage needs to delegate a discrete LLM sub-task (e.g. "summarise this for me"). Holocron's tools are deterministic Mongo / synonym-table lookups; there's no LLM sub-task to delegate.

**Execution modes** — Streaming vs. non-streaming. Streaming yields `AgentResponseUpdate` events as the agent generates; non-streaming yields a single `AgentResponse`. Holocron already uses streaming via the AGUI dialog (Design-020). Keep it.

**Human-in-the-loop** — `RequestPort` pattern + tool approval. Pauses the workflow on `RequestInfoEvent`. Holocron doesn't currently need HITL (the agent's enrichments are reviewed in-place via the Frontend's per-node panel after the run); could be added later for "approve before applying" flows but not in v2.0.

## Holocron's workload shape

Concrete, in numbers from the existing Anakin / Asajj runs:

| Property | Value | Implication |
|---|---|---|
| Pipeline stages | 5 (Discovery → Bundling → Extraction → Consolidation → Apply) | Already a workflow. No change needed at this level. |
| LLM stages | 1 (Extraction) | Multi-agent patterns add complexity for a single-LLM workload. |
| Agent count needed | 1 (the tool-using extractor) | "Multi-agent orchestration" is the wrong frame. |
| Batches per run | 5–50 (typical) up to ~500 (Sidious-class) | Per-batch parallelism is the lever for throughput. |
| Per-batch independence | Mostly independent — same target node, different chunks | Fan-out is safe; cross-batch dedup happens in the post-fan-in aggregator. |
| Per-batch latency | LLM call (~10–30 s) + N tool calls (~50–200 ms each) | Concurrent fan-out scales near-linearly until rate limit. |
| Tool calls per batch | ~10 facts × ~5 calls/fact = ~50 | Per-batch tool-call latency dominates if synchronous. |
| Resume-on-restart | Required (Anakin runs ~30 min) | Per-executor checkpointing must survive process restart. |
| LLM provider | OpenAI (gpt-4o-mini for Holocron) | Concurrency cap via the OpenAI rate-limit headers; `Microsoft.Agents.AI.OpenAI` honours them. |

The shape is **fan-out/fan-in over batches** — not a collaboration of differently-skilled agents. Many copies of the same agent doing the same thing on different inputs. That's the textbook concurrent-pattern shape, but expressed as a *workflow graph* not as a *multi-agent orchestration*.

## Pattern-to-workload fit matrix

Numerical scoring 0–3 (0 = poor fit, 3 = ideal). Justifications below the table.

| Pattern | Workload-shape fit | C# support | Implementation cost | Complexity tax | Total |
|---|---|---|---|---|---|
| Sequential orchestration | 1 | 3 | 3 | 3 | 10 |
| Concurrent orchestration (multi-agent flavour) | 1 | 3 | 2 | 2 | 8 |
| Handoff orchestration | 0 | 3 | 1 | 0 | 4 |
| Group Chat orchestration | 0 | 3 | 1 | 0 | 4 |
| Magentic orchestration | 0 | 0 | 0 | 0 | 0 |
| **Custom workflow + AIAgentHostExecutor + concurrent fan-out** | **3** | **3** | **2** | **3** | **11** |
| Custom workflow + sub-workflow per batch | 2 | 3 | 1 | 2 | 8 |

Justifications:

- **Sequential orchestration** scores 1 on workload-fit because Sequential treats each agent invocation as a stage; we'd need to fake "stages" by chunking batches into a sequence, which serialises what should be parallel. C# supported, easy to wire, but the wrong shape.
- **Concurrent orchestration (multi-agent flavour)** is meant for "N different agents on the same input." Wrong direction — we want "1 agent on N different inputs." Could be retrofitted by making each batch a "different agent" instance, but that's an abuse of the abstraction.
- **Handoff** scores 0 on workload-fit because there's no specialisation domain. Implementation cost 1 because the framework provides `HandoffAgentExecutor` machinery, but using it requires inventing fake handoff rules. Complexity tax 0 because the result is harder to reason about than a flat workflow.
- **Group Chat** same story — no debate; the tool layer makes encoding decisions. Wrong frame.
- **Magentic** scores 0 across the board because **it's Python-only in C# 1.1.0**.
- **Custom workflow + AIAgentHostExecutor + concurrent fan-out** scores 3 on workload-fit (this IS the shape), 3 on C# support (all primitives are first-class C# 1.1.0), 2 on implementation cost (we need to build the fan-out edge topology and aggregator executor by hand — not turn-key but well-trodden), 3 on complexity tax (no spurious abstractions; one agent type, one workflow graph).
- **Sub-workflow per batch** is essentially the same as the recommendation but with each batch wrapped in its own inner workflow. Adds a superstep loop per batch. Buys nothing we don't already get from per-executor checkpointing on the agent host. Real cost: harder to reason about flow control across the boundary.

**Winner:** custom workflow + concurrent fan-out of `AIAgentHostExecutor` instances.

## Recommended architecture

### Workflow graph (high-level)

```
                                                      ┌──→ AgentHost(batch-0) ──┐
                                                      │                          │
HolocronStartExecutor                                  ├──→ AgentHost(batch-1) ──┤
  │                                                   │                          │
  ▼                                                   ├──→ AgentHost(batch-2) ──┤
HolocronContextDiscoveryExecutor                       │                          │     HolocronApplyExecutor
  │  (chunks discovered, neighbour fetched)            │      ...                ├──→  ┌─────────────────────┐
  ▼                                                   │                          │     │ cross-batch dedup   │
HolocronChunkBundlingExecutor                          ├──→ AgentHost(batch-N-2) ┤     │ flush to enrichment │
  │  (emits N HolocronBatch messages)                  │                          │     │ collections         │
  └──────────fan-out───────────────────────────────────┴──→ AgentHost(batch-N-1) ─┘     └─────────────────────┘
                                                                                             ▲
                                                                                             │
                                                                          all-batches-completed barrier
```

Five executor types in the workflow:

1. `HolocronStartExecutor` — workflow-entry adapter (existing).
2. `HolocronContextDiscoveryExecutor` — unchanged from Design-020.
3. `HolocronChunkBundlingExecutor` — unchanged from Design-020. **Now emits one `HolocronBatch` message per batch as a fan-out source.**
4. `HolocronAgentHostExecutor<N>` — a fan-out of `N` instances of the **same** `AIAgent` wrapped in `AIAgentHostExecutor`. Each instance receives one `HolocronBatch`, runs its own conversation against the LLM with the tool surface bound, and emits a `HolocronBatchResult` (list of staged proposals + tool-call trace).
5. `HolocronApplyExecutor` — barrier-on-all-batches-completed, then cross-batch dedup + write to `kg.enrichments` / `kg.edge_enrichments`. **Absorbs the old consolidator.** Validation is gone — it lived in the tools, which already enforced it.

### Why one agent type, many host instances

Microsoft Agent Framework's `AIAgentHostExecutor` ([docs](https://learn.microsoft.com/agent-framework/workflows/advanced/agent-executor)) wraps a single `AIAgent` for use in a workflow. Each host instance has its own session and its own message cache, so N hosts running the same agent definition still produce N independent conversations. We declare the agent once (with its tool surface) and add it to the workflow graph N times — one per batch.

The framework's per-host checkpointing serialises:

- The agent's session state.
- The current turn's emission config.
- Pending requests / function-call requests.

This means each batch's conversation is independently resumable. If the process dies during batch 7's `propose_edge` tool call, restart picks up batch 7 mid-conversation; batches 0–6 are already complete in `kg.enrichments`; batches 8+ haven't started yet.

### Concurrency cap

`WorkflowBuilder` doesn't ship a built-in degree-of-parallelism limiter, so the cap goes on the bundling executor: emit at most `K` batches at a time, await completion, then emit the next `K`. Choose `K` to respect the OpenAI rate-limit budget for the model in use.

For the Asajj/Anakin profile (gpt-4o-mini, ~50 rpm typical):

- 50–500 batches per run.
- Per-batch LLM call ~15 s.
- Cap `K = 5`. Run completes in ~`(N/K) × 15s` = 150–1500 s = 2.5–25 minutes wall-clock.

This is comparable to or faster than the current sequential per-batch loop.

### Tool surface lives on the agent, not the workflow

Critical detail. The six tools from Design-025 (`resolve_entity`, `find_canonical_label`, `check_existing_edges`, `get_template_schema`, `propose_edge`, `propose_property`, `suggest_new_label`) are bound to the `AIAgent` via `AITool` instances at construction time:

```csharp
var holocronAgent = chatClient
    .CreateAIAgent(
        instructions: HolocronSystemPrompt.V2,
        tools: [
            EntityResolutionTool.Create(nodes),
            CanonicalLabelTool.Create(synonyms),
            ExistingEdgesTool.Create(edges),
            TemplateSchemaTool.Create(),
            ProposeEdgeTool.Create(stagingStore, validator),
            ProposePropertyTool.Create(stagingStore, validator),
            SuggestNewLabelTool.Create(suggestionsStore),
        ]);
```

The tools' write side (`propose_edge`, `propose_property`, `suggest_new_label`) writes into a per-batch staging store keyed by `(batchIndex, proposalId)`. The `HolocronApplyExecutor` reads all batches' staging entries, dedups, and flushes to the live collections.

### What the agent loop looks like

The framework runs the tool-using loop automatically when the agent is invoked. Each call to `agent.RunAsync(messages, ...)` returns when the model emits a final message (no more tool calls). Internally:

```
agent.RunAsync(messages):
  while True:
    response = chatClient.GetChatResponse(messages, tools)
    if response is final assistant message (no tool calls):
        return response
    for each tool_call in response.tool_calls:
        result = invoke_tool(tool_call.name, tool_call.args)
        messages.append(tool_result(result))
    messages.append(response)
```

We don't write this loop ourselves — `AIAgent.RunAsync` does it. We just bind the tools, give it a chunk batch as the user message, and read out the staged proposals afterwards.

### Streaming and the Frontend dialog

The current Frontend dialog (Design-020 Stage A) uses AGUI streaming events to show progress. With `AIAgentHostExecutor`'s `EmitAgentUpdateEvents = true`, every `AgentResponseUpdate` becomes a `WorkflowEvent`. The existing dialog subscribes via the workflow's event stream — we just route per-batch events with a `batchIndex` tag so the UI can show "Batch 7/50: agent calling resolve_entity('bounty hunter')…".

This is a UX upgrade for free: the user sees the agent's reasoning trace live, instead of a 30-second silent wait followed by a structured-output blob.

## What about the C# Magentic gap?

If we ever need Magentic-shaped dynamic planning in C# — say, a "plan the enrichment campaign" step that decides which target nodes to visit and in what order based on observed evidence — we can either:

1. Wait for the framework's C# Magentic support to ship (tracking [agent-framework GitHub repo](https://github.com/microsoft/agent-framework)).
2. Build a hand-rolled planner agent + dispatcher executor in our workflow. The framework's lower-level primitives (executors, edges, agent host executors) are sufficient to implement the pattern; we'd just be doing what `MagenticBuilder` does for us in Python.

For Holocron v2.0 specifically, we don't need this. The campaign-planning question is upstream: which target nodes get enhanced, with what budget, on what cadence. That's already handled by the per-node trigger from the Frontend ("click Enhance on this page") and by Hangfire-scheduled batch enhancement runs. The agent inside the run doesn't need to plan — it executes a known workload (the chunks for one node) with a known toolset.

## Updates to Design-025

Design-025's "Proposed architecture" section currently sketches:

```
HolocronContextDiscoveryExecutor      ── unchanged
HolocronChunkBundlingExecutor          ── unchanged
HolocronAgentLoop                       ── REPLACES HolocronProposalExtractorExecutor
HolocronApplyExecutor                   ── simplified (consolidator merges in)
```

Replace with:

```
HolocronContextDiscoveryExecutor               ── unchanged
HolocronChunkBundlingExecutor                  ── unchanged; now emits N batches as fan-out
HolocronAgentHostExecutor (×N, fan-out)        ── one per batch; same AIAgent definition
HolocronApplyExecutor                          ── fan-in barrier + cross-batch dedup + write
```

Plus three new lines in the "Migration path" section:

- **Phase A** addition: when implementing the read tools, also build the `AIAgent` factory — single source of truth for system prompt + tool bindings.
- **Phase B** addition: the new extractor IS the workflow's fan-out node, not a single-execution-per-run. Wire `WorkflowBuilder` so bundling emits multiple `HolocronBatch` messages and the agent host executor type appears once with multiplicity N.
- **Phase C** addition: the consolidator stops being its own executor — it merges into apply. Apply becomes a fan-in barrier executor that reads all batches' staging entries from the per-batch state, then dedups + writes.

## Risks specific to this orchestration choice

1. **Concurrency starvation.** If the OpenAI rate limit is hit, batches stall. **Mitigation:** the bundler caps in-flight batches at `K`, and the `Microsoft.Agents.AI.OpenAI` client retries on 429 with exponential backoff. We surface "rate-limited, waiting…" in the dialog so users know it's not stuck.
2. **Staging-store coupling.** All batch hosts write into the same staging store. If two batches stage edges for the same `(from, to, label)`, they don't see each other's staging — by design (the cross-batch dedup is the apply step's job). **Risk:** if a tool's validation depends on "has this been staged elsewhere already?" we'd need a lock or a shared view. **Mitigation:** the validation tools (`propose_edge`, `propose_property`) only check against `kg.nodes` / `kg.edges` / `kg.enrichments` (the persistent state), not against other batches' staging. Cross-batch duplicates are merged at apply, not rejected at stage time. This is the same dedup model as the current consolidator.
3. **Per-host session size.** Each agent host caches the conversation. For a chunk batch with 50 chunks × 4 KB = 200 KB of user prompt, plus N tool-call round-trips (each adds tokens), the session grows. The 16 MB Mongo checkpoint limit could bite for Sidious-class runs. **Mitigation:** per-batch sessions are independent and short-lived (~10 turns). 16 MB is generous. If a batch hits the ceiling we cap chunks-per-batch lower in the bundler.
4. **AIAgentHostExecutor checkpoint serialization.** The host serialises agent session state on each superstep. If sessions are large or checkpoint frequency is high, this can be expensive. **Mitigation:** the framework only checkpoints at superstep boundaries; with one agent invocation per batch, a batch is one superstep. Checkpointing per-batch is cheap.
5. **No built-in degree-of-parallelism cap.** We have to enforce concurrency in our bundler. Easy to get wrong (forget the cap → blow the OpenAI rate limit). **Mitigation:** unit-test the bundler's emission cadence; integration-test against a mock LLM client that records max-concurrent-call count.
6. **Streaming + checkpointing interaction.** The agent host both streams `AgentResponseUpdate`s and checkpoints. The framework docs note that checkpointing during streaming serialises the "current turn's emission configuration." We need to verify resume-mid-batch produces a clean stream restart, not duplicate events. **Mitigation:** test resume scenarios on a 3-batch run with a mid-batch kill.

## Implementation outline

### Phase A.1 — Build the `AIAgent` factory (1–2 hours)

```csharp
public sealed class HolocronAgentFactory
{
    readonly IChatClient _chatClient;
    readonly IMongoDatabase _db;
    readonly LabelSynonymTable _synonyms;
    readonly TemplateSchemaProvider _templates;
    readonly HolocronStagingStore _staging;
    readonly LabelSuggestionStore _suggestions;
    readonly AliasNameClassifier _aliasClassifier;

    public AIAgent CreateForBatch(int pageId, string targetType, int batchIndex)
    {
        var tools = new AITool[]
        {
            EntityResolutionTool.Create(_db),
            CanonicalLabelTool.Create(_synonyms),
            ExistingEdgesTool.Create(_db, pageId),
            TemplateSchemaTool.Create(_templates, targetType),
            ProposeEdgeTool.Create(_staging, _db, batchIndex),
            ProposePropertyTool.Create(_staging, _db, _aliasClassifier, batchIndex),
            SuggestNewLabelTool.Create(_suggestions, batchIndex),
        };

        return _chatClient.CreateAIAgent(
            instructions: HolocronSystemPrompt.V2,
            tools: tools);
    }
}
```

### Phase A.2 — Wire the workflow with fan-out (2–3 hours)

```csharp
var workflow = new WorkflowBuilder(startExecutor)
    .AddEdge(startExecutor, discoveryExecutor)
    .AddEdge(discoveryExecutor, bundlingExecutor)
    // bundlingExecutor emits N HolocronBatch messages
    // each one fans out to a fresh agent host instance
    .AddFanOutEdges(
        bundlingExecutor,
        targetFactory: batch =>
        {
            var agent = agentFactory.CreateForBatch(
                pageId: state.PageId,
                targetType: state.TargetType,
                batchIndex: batch.Index);
            return new AIAgentHostExecutor(agent, new AIAgentHostOptions
            {
                EmitAgentUpdateEvents = true,
                EmitAgentResponseEvents = true,
            });
        },
        maxConcurrent: 5)
    .AddFanInEdge(
        from: typeof(AIAgentHostExecutor),
        to: applyExecutor,
        barrier: AllBatchesComplete)
    .Build();
```

(Pseudocode — the framework's actual fan-out API may use `WithEdgeGroup` or per-instance executor IDs; verify against `Microsoft.Agents.AI.Workflows` 1.1.0.)

### Phase A.3 — Implement the seven tools (2–3 hours)

Each tool is a thin `AITool` over a Mongo lookup or a synonym-table lookup. Synchronous validation, return a typed result envelope `{ ok: bool, data?, reason? }` so the agent sees rejection reasons inline.

### Phase A.4 — Apply executor (1–2 hours)

Reads per-batch staging entries, runs cross-batch dedup (same logic as the current consolidator's dedup pass, minus all the validation), writes to `kg.enrichments` / `kg.edge_enrichments`.

### Total estimate

~7–10 hours of focused implementation, plus testing. Matches the Design-025 "~2 days" estimate when you include integration tests, parallel comparison runs against v1.3.0, and Frontend wiring for the per-batch streaming events.

## Decision points

For Design-026 specifically — beyond the five points in Design-025:

6. **Concurrent fan-out is the orchestration shape.** Not Sequential / Concurrent / Handoff / Group Chat / Magentic. Custom workflow graph with multiplicity on the agent-host executor.
7. **One `AIAgent` definition, N host instances.** The agent is constructed once per batch (so per-batch tool closures over `batchIndex` etc.) but the system prompt + tool surface is the same for every batch.
8. **Concurrency cap belongs in the bundler.** Not in the agent host. Bundler emits at most `K` batches concurrently; default `K = 5`, configurable per-deployment.
9. **No sub-workflows.** Flat workflow graph. Per-executor checkpointing already gives us per-batch isolation.
10. **Magentic-like planning is out of scope for v2.0.** Revisit only if the campaign-planning use case (which target nodes to visit) becomes agent-driven rather than user-/Hangfire-driven.

If 6–10 are confirmed, Design-025's Phase A–D plan is unchanged in shape; the implementation details just get the concrete fan-out topology described above.
