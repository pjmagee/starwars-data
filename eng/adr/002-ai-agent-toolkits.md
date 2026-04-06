# ADR-002: AI Agent Toolkits on Microsoft Agent Framework

**Status:** Accepted
**Date:** 2026-04-05
**Decision maker:** Patrick Magee

## Context

The Ask AI feature (`/kernel/stream`) is a single conversational agent that must answer
free-form questions about Star Wars lore using only trustworthy, grounded data. It has to:

- Resolve entity names to canonical PageIds.
- Fetch structured facts (properties, dates, relationships).
- Aggregate knowledge-graph data for charts and analytics.
- Retrieve narrative passages for "why/how" questions.
- Emit rich frontend components (tables, charts, graphs, timelines, infoboxes, markdown,
  Aurebesh) rather than plain text, so the Blazor UI renders structured responses.

We need a disciplined way to expose ~50 capabilities to the model without turning the
prompt into a dumping ground, and without letting the LLM hallucinate numbers or IDs.

## Decision

### Framework: Microsoft Agent Framework (.NET)

We use the **Microsoft Agent Framework** (`Microsoft.Agents.AI`) for the agent runtime,
composed with **Microsoft.Extensions.AI** (`IChatClient`) and the OpenAI SDK.

Rationale:

- Agent Framework is the unified successor to Semantic Kernel and AutoGen, with
  first-class support for `AIAgent`, `AITool`, middleware, and composable pipelines.
- `M.E.AI.IChatClient` is the portable abstraction we already use everywhere else
  (BYOK, OpenTelemetry, guardrail middleware).
- `AIFunctionFactory.Create(delegate, name, description)` lets us register plain C#
  methods as tools with strongly typed parameters and JSONSchema auto-generation.
- It composes cleanly with MCP clients (`McpClient`) so we can graft external MCP
  tools (e.g. MongoDB MCP `find`/`aggregate`/`count`) alongside native tools.
- **No Semantic Kernel.** SK packages are not permitted in this repo. See the
  feedback memory `no_semantic_kernel`.

Wiring lives in
[src/StarWarsData.ApiService/Program.cs](../../src/StarWarsData.ApiService/Program.cs),
culminating in:

```csharp
return chatClient
    .AsAIAgent(instructions: AgentPrompt.Instructions, tools: tools)
    .AsBuilder()
    .UseStarWarsTopicGuardrail(classifierClient, aiStatus, guardrailLogger)
    .Build();
```

The agent is streamed to the frontend via **AGUI** (`app.MapAGUI("/kernel/stream", ...)`).

### Toolkit layering

Tools are grouped into cohesive **toolkits** under
[src/StarWarsData.Services/AI/Toolkits/](../../src/StarWarsData.Services/AI/Toolkits/).
Each toolkit is a plain class whose public methods carry `[Description]` attributes
and an `AsAIFunctions()` method that yields `AITool` instances via
`AIFunctionFactory.Create(method, snake_case_name)`.

| Toolkit                      | Role                                  | Tool count | Returns                                    |
| ---------------------------- | ------------------------------------- | ---------- | ------------------------------------------ |
| `GraphRAGToolkit`            | Primary KG lookup & semantic search   | 12         | JSON facts                                 |
| `KGAnalyticsToolkit`         | KG-backed aggregation for charts      | 16         | JSON numeric data                          |
| `DataExplorerToolkit`        | Raw `Pages` / infobox fallback        | 10         | JSON facts                                 |
| `ComponentToolkit`           | Frontend render descriptors           | 8          | `TableDescriptor`, `ChartDescriptor`, etc. |
| `RelationshipAnalystToolkit` | ETL-time edge extraction (not in Ask) | 8          | JSON                                       |
| Ad-hoc (`keyword_search`)    | Wiki keyword fallback                 | 1          | JSON                                       |
| MongoDB MCP (filtered)       | Raw `find`, `aggregate`, `count`      | 3          | MCP tool results                           |

Only the **first four** toolkits plus `keyword_search` and the MCP tools are wired
into the Ask AI agent. `RelationshipAnalystToolkit` powers a separate ETL-time
extraction agent and is registered through its own path.

### Tool categories and agent flow

The toolkits map to three phases the agent moves through on every question:

1. **Discover**
   `list_infobox_types`, `list_entity_types`, `list_relationship_labels`,
   `describe_relationship_labels`, `describe_entity_schema`,
   `list_labels_by_category`, `list_timeline_categories`.
   The agent learns what values are legal before it queries.
2. **Retrieve / Aggregate**
   `search_entities`, `get_entity_properties`, `get_entity_relationships`,
   `find_entities_by_year`, `semantic_search`, and the full `KGAnalyticsToolkit`.
   All factual numbers are produced by real MongoDB aggregations, never guessed.
3. **Render**
   Exactly one (or a few) `render_*` calls from `ComponentToolkit` describe HOW
   the answer should be shown. These tools do not query data — they receive the
   results of step 2 as arguments.

This staged model is enforced by the tool descriptions themselves: analytics tools
tell the agent to "call X first", and render tools tell the agent to aggregate
first and pass results. The class-level XML docs hoist the shared guidance so
per-tool descriptions do not repeat it.

### KG-first, not Pages-first

`GraphRAGToolkit` and `KGAnalyticsToolkit` query the pre-built knowledge graph
(`kg.nodes`, `kg.edges`, temporal facets). `DataExplorerToolkit` queries the raw
`Pages` collection and is only used when an answer needs an infobox field that
isn't projected into the KG. This matches the `kg_first` feedback memory —
new features should target the KG, not legacy timeline collections.

### Descriptor-based rendering

Render tools return **descriptors**, not rendered HTML. The frontend
(Blazor + MudBlazor) reads `TableDescriptor`, `ChartDescriptor`, `GraphDescriptor`,
`TimelineDescriptor`, `InfoboxDescriptor`, `TextDescriptor`, `AurebeshDescriptor`
off the `ComponentToolkit` state and paints them. The agent never emits raw HTML
or JSX, which keeps the contract between model and UI small and auditable.

## Documentation conventions for tools

The `[Description]` text is the ONLY thing the model sees for each tool, so its
quality is part of the product. Conventions:

- **Use C# raw string literals (`"""..."""`)** for multi-line descriptions. No
  `"foo" + "bar"` concatenation in `[Description]` attributes — it is a code smell
  when tooling and diff tools have to parse multi-fragment strings.
- **Hoist repetition into the class-level XML doc.** For example,
  `DataExplorerToolkit` documents the "infoboxType filters by `infobox.Template`,
  not a collection name" rule once on the class, instead of repeating it on every
  parameter.
- **Extract shared parameter blurbs to `const string` fields** for parameters that
  repeat verbatim across tools (`ContinuityParamDescription`,
  `InfoboxTypeParamDescription`, `ReferencesParamDescription`). This keeps
  per-tool descriptions short and guarantees consistency.
- **Lead with purpose, then show examples.** Every description starts with what
  the tool does and when to prefer it over alternatives, then gives 2-3 concrete
  examples with parameter shapes. Examples do more for tool-selection accuracy
  than abstract descriptions.
- **Cross-reference sibling tools.** Tools explicitly name the ones the agent
  should call first ("call `search_entities` first to get the PageId") and the
  ones to prefer instead ("for simple direct relationships, prefer
  `get_entity_relationships`"). This prevents overlapping tools from being
  chosen arbitrarily.
- **State the expected chart type** on analytics tools ("Best for: Bar, Pie,
  Radar") so the agent pairs aggregator and renderer correctly.
- **All tool names are snake_case** so JSONSchema parameter names stay uniform
  across native and MCP tools (the MCP bridge rewrites `-` to `_`).
- **All I/O is async.** Every tool method is `async Task<string>` returning
  JSON (or a typed descriptor for render tools) — no blocking calls inside
  the agent loop.

## Consequences

### Positive

- The agent has a single idiomatic pattern for adding capabilities: write a
  method on the right toolkit, decorate parameters, register in `AsAIFunctions()`.
- Tool descriptions live next to the implementation, so they evolve with the code.
- Shared boilerplate (continuity filter, infobox-type disclaimer, references
  parameter) is defined once per toolkit via `const` fields.
- Swapping the model provider or chat client goes through `IChatClient` and does
  not touch tool code.
- MCP tools plug into the same `AITool` list, so the agent cannot tell native
  tools from MCP tools.

### Negative / Trade-offs

- Each new tool costs prompt tokens on every call because descriptions are
  embedded in the system prompt. We currently ship ~50 tools, which is near the
  comfortable ceiling for a single agent. If we grow further we will need to
  split the agent (e.g. an analyst sub-agent and a renderer sub-agent), use
  `UseAIContextProviders` for conditional tool surfacing, or adopt
  routing/handoff patterns from Agent Framework.
- Tools that return JSON strings are not strongly typed on the return side. We
  accept this because the frontend only consumes typed descriptors from
  `ComponentToolkit`; everything else is LLM input.
- `[Description]` lives in attributes rather than XML docs, so IDE hover
  tooltips on toolkit methods are noisier than typical. This is the price of
  keeping the model-facing text next to the parameter declaration.

## Alternatives considered

- **Semantic Kernel plugins.** Rejected — see ADR-free feedback memory. SK is
  being subsumed by Agent Framework anyway.
- **Single monolithic toolkit.** Rejected; tool selection accuracy drops when
  unrelated tools share a class and the class-level XML doc can no longer hoist
  shared guidance.
- **Returning typed C# records from every tool.** Rejected for retrieval tools
  because the LLM consumes JSON anyway and `JsonSerializer.Serialize(anon)`
  gives us cheap schema evolution. Retained for render tools where the
  frontend is the real consumer.
- **XML doc comments (`///`) instead of `[Description]`.** Rejected — Agent
  Framework's `AIFunctionFactory` reads `[Description]` attributes for the
  JSONSchema it sends to the model; XML docs are not read by the framework.

## References

- Agent registration:
  [src/StarWarsData.ApiService/Program.cs](../../src/StarWarsData.ApiService/Program.cs)
- Toolkits:
  [src/StarWarsData.Services/AI/Toolkits/](../../src/StarWarsData.Services/AI/Toolkits/)
- Agent instructions: [src/StarWarsData.Services/AI/AgentPrompt.cs](../../src/StarWarsData.Services/AI/AgentPrompt.cs)
- Microsoft Agent Framework overview: <https://learn.microsoft.com/agent-framework/overview/agent-framework-overview>
- Microsoft Agent Framework .NET repo: <https://github.com/microsoft/agent-framework/tree/main/dotnet>
- Related: [ADR-001: Internal API Authentication](./001-internal-api-auth.md)
