# Design 012: AI Agent Tool-Call Efficiency

**Status:** Phase 1 shipped 2026-04-11
**Date:** 2026-04-11
**Owner:** Patrick Magee

## Context

A single user question — *"What strategic mistakes and blindspots allowed the Rebel Alliance to win at Endor?"* — produced **71 tool calls** before the agent finished. Logs from `apiservice` showed the failure mode clearly:

- A single trace (`b82a61b`) issued **8 sequential `semantic_search` calls** with reworded variants of the same query (lengths 134 → 99 → 35 → 54 → 63 → 72 → 47 → 61 chars).
- A second trace (`01f15bb`) issued another 2 (207, 193 chars).
- The 4-line log group per call (embed → dims → vectorSearch → results) was never interleaved, confirming the calls were *sequential across iterations*, not batched within one iteration. Worst-case fan-out, maximum latency, maximum cost.
- A real bug surfaced too: `KnowledgeGraphQueryService.SearchNodesAsync` crashed with `A CString cannot contain null bytes. (Parameter 'value')` from MongoDB.Driver, because the agent passed a query containing `\0`. The crash made the agent fall back to other tools, adding even more calls.

The framework was *not* enforcing any meaningful budget. `Microsoft.Extensions.AI.FunctionInvokingChatClient.MaximumIterationsPerRequest` defaults to **40**, and `Microsoft.Agents.AI.ChatClientAgent` was wrapping the chat client with that default automatically. The system prompt's "HARD LIMIT: 10 tool calls — STOP" rule was purely textual and the model ignored it.

## Root causes

1. **No code-level budget.** The "10-call limit" lived only in [AgentPrompt.cs:22](../../src/StarWarsData.Services/AI/AgentPrompt.cs#L22) as natural language with no enforcement, and the framework's own `MaximumIterationsPerRequest` was at its default 40. The model had ~7× more headroom than intended.
2. **Tool surface too large.** ComponentToolkit + DataExplorerToolkit + GraphRAGToolkit + KGAnalyticsToolkit + keyword_search + 3 MCP tools ≈ ~47 functions in one registry. More tools → noisier pattern-matching → more "one more lookup" loops, especially on lore questions where no single tool answers.
3. **System prompt was 265 lines.** Routing rules, anti-patterns, and per-tool examples were buried in one giant block. Attention dilution: the model glossed over the efficiency rule because it was line 22 of 265.
4. **Per-tool guidance lived in the wrong place.** OpenAI/Anthropic function-calling sends each tool's `description` field with the schema at decision time. Putting routing rules in the system prompt instead of the tool descriptions hides them at the moment they matter.
5. **Lore questions are the worst case.** Profile/browse questions have a single fast-path tool (`render_infobox`, `render_table`). Lore questions like "why did X happen" don't, so the model fans out across `semantic_search`, `get_entity_*`, `traverse_graph` looking for narrative material — and without a budget, that loop can run forever.
6. **Null-byte crash compounded the loop.** Every time `search_entities` threw on a `\0` query, the agent retried via a different tool, adding extra calls to the already-runaway sequence.

## Phase 1 — Boring fixes (shipped 2026-04-11)

### 1. Null-byte sanitization at the boundary

Added [`MongoSafe.Sanitize`](../../src/StarWarsData.Services/Shared/MongoSafe.cs) — strips `U+0000`, the rest of the C0 control range (except `\t \n \r`), and unpaired surrogates from any string about to flow into a MongoDB regex or filter. Applied at every LLM-input site:

- [KnowledgeGraphQueryService.SearchNodesAsync](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs#L623) and the other regex builders in the same file
- [DataExplorerToolkit.EscapeRegex](../../src/StarWarsData.Services/AI/Toolkits/DataExplorerToolkit.cs#L33)
- [MapService.SearchGridAsync](../../src/StarWarsData.Services/GalaxyMap/MapService.cs#L107)

Removes the retry-cascade trigger entirely.

### 2. Code-enforced tool-call budget

Two layers, framework-native:

**Framework iteration cap.** [Program.cs](../../src/StarWarsData.ApiService/Program.cs) now wires `FunctionInvokingChatClient` explicitly with `MaximumIterationsPerRequest = 12` (down from the default 40) and `AllowConcurrentInvocation = true` so the model *can* batch parallel tool calls within one iteration when it chooses to. The chat client is built with `UseProvidedChatClientAsIs = true` on `ChatClientAgentOptions` so the agent doesn't double-wrap with a second `FunctionInvokingChatClient` at default settings.

**Function-calling middleware ([ToolCallBudgetMiddleware](../../src/StarWarsData.Services/AI/ToolCallBudgetMiddleware.cs)).** Per-tool-call counter using `AsyncLocal<RunCounter>` to scope per agent run. Logs every call at Debug, emits a structured Warning at the soft threshold (10), and sets `FunctionInvocationContext.Terminate = true` when the hard threshold (15) is hit. Wired via the same `AIAgentBuilder.Use(...)` pipeline as the existing `StarWarsTopicGuardrail`:

```csharp
return chatClient.AsAIAgent(agentOptions)
    .AsBuilder()
    .UseToolCallBudget(softWarnAt: 10, hardLimit: 15, logger: budgetLogger)
    .UseStarWarsTopicGuardrail(classifierClient, aiStatus, guardrailLogger)
    .Build();
```

The framework cap is the hard ceiling that survives every prompt regression. The middleware adds diagnostic value (which tools were called, in what order) and a finer-grained termination point (15 individual calls vs 12 round trips, since one round trip can request multiple tools).

### 3. Routing guidance moved from system prompt to tool descriptions

Each tool's `[Description]` attribute is sent to the model with the function schema at the moment of choice. Burying the same guidance 200 lines deep in a system prompt is strictly worse. Migrated routing rules and anti-patterns onto the high-traffic tools:

- **`semantic_search`** ([GraphRAGToolkit.cs](../../src/StarWarsData.Services/AI/Toolkits/GraphRAGToolkit.cs)) — added explicit "DO NOT FAN OUT" rule with the Endor case as the cautionary example, plus a "max 2 calls per question" cap and "synthesize, don't reword" guidance.
- **`get_entity_properties`** — strengthened the batching anti-pattern: looping one ID at a time is "always wrong"; one call with N IDs.
- **`search_entities`** — added the routing matrix (profile vs relationship vs timeline) and a "never call twice for the same entity" rule.
- **`render_chart`** — added the "every value must come from a tool result" rule and a chart-type cheat sheet.
- **`render_markdown`** — added the routing matrix for narrative answers and an explicit "STOP CONDITION: end the turn after rendering" instruction.

### 4. System prompt trimmed

[AgentPrompt.cs](../../src/StarWarsData.Services/AI/AgentPrompt.cs) shrunk from 265 lines to ~55. What stayed:

- Identity, safety, message-metadata convention
- The cross-cutting hard rules: budget, no fabrication, render-tool stop condition, mobileSummary requirement
- The two-calendar concept (galactic vs CE) and the temporalFacets concept
- A pointer telling the model to trust the per-tool descriptions

What left:

- The "WHEN TO USE WHAT" routing matrix (now lives on individual tool descriptions)
- Per-tool examples (now on the tools)
- Per-tool anti-patterns (now on the tools)
- The duplicated "render tool" listing (the schemas tell the model what's available)

## Success criteria

Re-run the Endor question after Phase 1 and measure:

- **Target:** ≤ 6 tool calls. The 10-call soft warning should not fire.
- **Hard ceiling:** 15 (middleware) / 12 iterations (framework). Hitting either is a failure that should be investigated, not normalized.
- **`semantic_search` calls per question:** ≤ 2.
- **`get_entity_properties` calls per question:** ≤ 1 (always batched).

The middleware emits structured logs (`ToolCallBudget: soft threshold 10 reached at <tool>`) so regressions are visible in OTel without re-running the trace by hand.

## Phase 2 — Handoff experiment (deferred, only if needed)

**Trigger:** if Phase 1 measurements show that lore questions still hit 10+ calls after the prompt and budget changes, or if a new question category emerges with chronic fan-out.

**Pattern:** [Microsoft Agent Framework Handoff Orchestration](https://learn.microsoft.com/agent-framework/workflows/orchestrations/handoff). Specifically the C# `HandoffAgentExecutor` ([docs](https://learn.microsoft.com/agent-framework/workflows/orchestrations/handoff#the-handoff-agent-executor)), which:

- Implements a mesh topology — agents transfer control directly with no central orchestrator overhead per round (unlike Group Chat which star-topology routes via an orchestrator each turn).
- Auto-injects handoff tools onto each specialist agent based on the configured handoff rules.
- Filters handoff tool calls and results out of the forwarded conversation history, so internal routing mechanics don't pollute the receiving agent's context.
- Pairs naturally with the existing `StarWarsTopicGuardrail` agent middleware — the guardrail stays the outermost layer, then the router agent decides which specialist to hand to.

**Why handoff and not Group Chat / Sequential / Magentic / Concurrent.** Microsoft's own [Workflow orchestrations](https://learn.microsoft.com/agent-framework/workflows/orchestrations/) overview and the [AI agent orchestration patterns](https://learn.microsoft.com/azure/architecture/ai-ml/guide/ai-agent-design-patterns) guide explicitly call out the pitfalls that would apply here:

> 1. Creating unnecessary coordination complexity by using a complex pattern when basic sequential or concurrent orchestration would suffice.
> 2. Adding agents that don't provide meaningful specialization.
> 3. Overlooking latency impacts of multiple-hop communication.
> 8. Consuming excessive model resources because context windows grow as agents accumulate more information…

[Group Chat](https://learn.microsoft.com/agent-framework/workflows/orchestrations/group-chat) is for "iterative refinement, collaborative problem-solving, multi-perspective analysis" — it doubles round-trips per turn and broadcasts every agent's response to every other agent. Wrong shape for a routing problem. [Magentic](https://learn.microsoft.com/agent-framework/workflows/orchestrations/magentic) (manager dynamically planning) is heavier still and designed for open-ended research where the plan is unknown. [Sequential](https://learn.microsoft.com/agent-framework/workflows/orchestrations/sequential) Research → Render is overkill — render is already a tool, not a phase. [Concurrent](https://learn.microsoft.com/agent-framework/workflows/orchestrations/concurrent) doesn't help because the toolkits already batch internally (`get_entity_properties` accepts 20 IDs, etc.).

**Proposed split.** A thin classifier picks the lane, then exactly one specialist runs to completion with a small tool surface and a focused 30-line prompt:

| Specialist | Tools | Target questions |
| --- | --- | --- |
| `ProfileAgent` | `search_entities`, `search_pages_by_name`, `render_infobox`, `render_table` | "Tell me about X", "Compare X and Y", "Show all lightsabers" |
| `LoreAgent` | `semantic_search`, `get_entity_timeline`, `render_markdown` | "Why did X happen", "What was the philosophy of X", explanation/aftermath questions |
| `AnalyticsAgent` | `count_*`, `group_*`, `top_connected_entities`, `compare_entities`, `render_chart`, `render_data_table` | "How many X", "Top N X", "Distribution of X", radar comparisons |
| `GraphAgent` | `search_entities`, `get_relationship_types`, `get_entity_relationships`, `traverse_graph`, `find_connections`, `render_graph`, `render_path` | "How is X related to Y", family trees, hierarchies, path queries |
| `TemporalAgent` | `find_entities_by_year`, `get_galaxy_year`, `find_by_lifecycle_transition`, `count_by_year_range`, `render_timeline` | "What happened in 19 BBY", "Wars during X", time-bucketed counts |

Each specialist has 5–10 tools instead of 47. The Endor question routes to LoreAgent, where the worst possible fan-out is bounded by what's actually in that agent's toolbox.

**Cost.** Handoff adds one extra LLM call per question (the routing classifier) but each specialist runs much shorter. Net latency should improve on lore questions and stay roughly flat on profile/browse questions. Worth measuring against Phase 1 baselines before committing.

**Build effort.** Estimated 1–2 days. Existing groundwork in our favour:

- The topic guardrail already proves the `AIAgentBuilder.Use(runFunc, runStreamingFunc)` middleware pattern works with the AGUI streaming endpoint.
- All toolkits are already class-based and resolved from DI — splitting into per-specialist tool registries is just composition.
- `ToolCallBudgetMiddleware` is per-agent so each specialist gets its own budget out of the box.

**Don't start until** the Phase 1 measurements show Phase 1 isn't enough. Cheap fixes first; structural changes only when measurements demand it.

## What we explicitly chose NOT to do

- **Custom `DelegatingChatClient` to count tool calls.** Reinventing the wheel — `FunctionInvokingChatClient.MaximumIterationsPerRequest` already exists, and a function-calling middleware on `AIAgentBuilder` is the framework-blessed way to inspect individual calls.
- **Group Chat orchestration.** See "Why handoff and not Group Chat" above.
- **Tool description token-budget paranoia.** Yes, expanding per-tool descriptions costs prompt tokens on every request. The tradeoff is acceptable: ~3-5k extra prompt tokens vs the ~70× tool-call cost we measured. Will revisit if a token regression actually shows up.
- **Removing tools to shrink the surface.** All ~47 tools are doing real work for some question category. The fix is routing, not amputation.

## References

- The 71-call event: trace `b82a61b` (semantic_search × 8) and trace `01f15bb` (semantic_search × 2 more) on `apiservice-uhfnkzma`, captured 2026-04-11 via the Aspire MCP.
- [Microsoft.Extensions.AI FunctionInvokingChatClient](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient)
- [MaximumIterationsPerRequest](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient.maximumiterationsperrequest)
- [Agent Framework Middleware — Function calling middleware](https://learn.microsoft.com/agent-framework/agents/middleware/#function-calling-middleware)
- [ChatClientAgentOptions.UseProvidedChatClientAsIs](https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.chatclientagentoptions.useprovidedchatclientasis)
- [Workflow orchestrations overview](https://learn.microsoft.com/agent-framework/workflows/orchestrations/) — sequential, concurrent, handoff, group chat, magentic
- [Handoff orchestration](https://learn.microsoft.com/agent-framework/workflows/orchestrations/handoff)
- [AI agent orchestration patterns (Azure architecture guide)](https://learn.microsoft.com/azure/architecture/ai-ml/guide/ai-agent-design-patterns) — common pitfalls and anti-patterns
- ADR-002: [AI Agent Toolkits on Microsoft Agent Framework](../adr/002-ai-agent-toolkits.md)
