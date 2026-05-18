# ADR-008: Agent tools consume a request-scoped filter context

**Status:** Accepted
**Date:** 2026-04-30
**Decision maker:** Patrick Magee
**Cross-refs:** [ADR-002 AI Agent Toolkits](002-ai-agent-toolkits.md), [Design-022 Page-Aware Copilot Sidebar](../design/022-galaxy-map-copilot.md), [Design-029 Continuity-aware Agent Tools](../design/029-agent-filter-context.md)

> **Update (2026-05-18):** Option C is no longer a proposal — Phase 1 shipped on
> the `feature/copilot-sidebar` branch (merged to `main`, commits `616ae44dd2` /
> `4fae7aad1d`). `ICurrentRequestContext`
> (`src/StarWarsData.Services/AI/RequestContext/ICurrentRequestContext.cs`),
> `AguiEnvelopeParserMiddleware`
> (`src/StarWarsData.Services/AI/RequestContext/AguiEnvelopeParserMiddleware.cs`),
> and GraphRAGToolkit continuity-defaulting-from-context are live. Design-022
> (the copilot sidebar / envelope wire format) is now Shipped (Phases 1–2);
> Design-029 is Partially shipped — only Phase 1 of its rollout landed
> (context service + middleware + GraphRAGToolkit defaulting). Phases 2–4 of
> Design-029 (the remaining ~5–10 tools, `get_galaxy_year` return filtering,
> Mongo MCP wrappers, prompt-line removal) are **not yet built**; the
> "Consequences" section below still describes them as pending work.

## Context

Both AI agents in this codebase (`AskAIAgent` at `/kernel/stream`, `CopilotAgent`
at `/copilot/stream`) receive global UI filter state as bracketed envelopes in
the user's message:

```text
[CONTINUITY: Canon|Legends|Both] [PAGE: …] [SUBJECT: …] …
```

The system prompt instructs each agent to **pass continuity to tool calls**
when the tool accepts a `continuity` parameter. This works inconsistently:

- Many tools accept a `continuity` parameter, but the agent forgets to pass it
  on multi-step plans.
- Some tools — notably `get_galaxy_year`, `find_entities_by_year`, and the
  Mongo MCP read tools — return continuity-tagged data but have no
  `continuity` parameter at all. They hand back everything; the agent is
  expected to filter in its head before composing the answer.
- Even when tools do filter, the agent sometimes mentions filtered-out
  entities in prose ("the rest is mostly Legends-only…"), leaking content
  the user explicitly toggled off.

A real example from the copilot during testing (continuity = Canon, year =
−100,000 BBY, galaxy map):

> *"In Canon, the visible entries are concentrated around Coruscant… The rest
> of the activity on this snapshot is largely Legends-only, including things
> like Abeloth, Daughter/Legends, Son/Legends, and major ancient sites such as
> Centerpoint Station and the Dawn Pyramid of Aargau."*

The user reasonably asked: *"Why does the agent even **know** about Legends
material if I have Legends filtered off?"* The honest answer is that the data
layer is continuity-blind — the global UI filter only constrains what the
**page** renders, not what the **agent's tools** return.

We need a single architectural pattern that makes the agent's view of the world
consistent with the user's filter selection, without depending on the agent to
remember to pass parameters.

The pattern generalises beyond continuity: `realm` (Star Wars / Real World) is
already a parallel facet, and future filters (era, lens, etc.) will want the
same treatment.

## Options Considered

### Option A: Prompt rule only — "drop filtered content from your answer"

Tighten the system prompt to make continuity a hard answer-shape filter:
*"Drop filtered entities silently from your answer; do not say you filtered
them out."*

This is what landed on `feature/copilot-sidebar` as a stop-gap. It works most
of the time but is brittle:

- The agent still pays the LLM cost of receiving filtered-out content in tool
  results before discarding it.
- Tool-call arguments still include filtered-out entity ids in chains where
  one tool's output becomes the next tool's input.
- Any prompt slip ("for completeness, here's what's missing…") leaks the
  filtered content back into the answer.
- Doesn't generalise — every new filter facet means another prompt rule and
  another opportunity to leak.

**Rejected as the only fix** but kept as a belt-and-braces fallback alongside
whichever option is chosen below.

### Option B: Per-tool explicit `continuity` parameter

Status quo. Every tool that touches continuity-tagged data exposes a
`continuity` parameter; the agent passes it on every call.

**Rejected because:**

- Half the relevant tools don't have the parameter today. Adding it requires
  touching ~15 tool signatures and their description prompts.
- Agent reliability: even with the parameter present, the agent forgets to
  pass it on chained calls (~20% of the time in observed traces).
- Mongo MCP tools are external — we don't control their signatures and can't
  add a `continuity` filter there.
- Doesn't generalise to other facets without proportional churn.

### Option C: Request-scoped ambient filter context (proposed)

Add an `ICurrentRequestContext` service that exposes the active filters for
the duration of a single AGUI request. Wire it via:

1. **Middleware on `/kernel/stream` and `/copilot/stream`** parses the most
   recent user message body, extracts `[CONTINUITY: …][REALM: …]…`, and stores
   the parsed filter set on a scoped service.
2. **Tools read from the service** at execution time — e.g. KG queries default
   `continuity` from the context when the parameter is null; `get_galaxy_year`
   filters its return value against the context before serialising.
3. **The agent never sees filtered-out content.** No prompt rule needed; the
   data the agent consumes already matches the user's filter.

**Rejected concerns:**

- *"Ambient state is hard to reason about"* — the lifetime is exactly one
  HTTP request and the only consumers are tool implementations, both of which
  already run inside the request scope. Tracing a tool result back to its
  filter source is one stack frame.
- *"What about cases where the agent legitimately wants both?"* — the
  envelope already carries `Both` / `Unknown` for that case; the service
  exposes the raw value, and tools can opt out of the default for explicit
  cross-continuity queries (rare).

This is the chosen direction.

### Option D: Filter at the agent middleware (post-tool)

Wrap every tool result through a filter pass that reads the envelope and
strips non-matching items.

**Rejected because:** still pays the cost of fetching the filtered-out content,
and the wrapper has to understand the shape of every tool's return type to
know which fields are continuity-tagged. Higher complexity than Option C with
no real upside.

## Decision

**Adopt Option C** — request-scoped ambient filter context, read by tools at
execution time. Keep Option A's prompt rule as a defence-in-depth fallback
for any tool that hasn't yet been wired to the context.

Implementation details, scope, and rollout plan live in
[Design-029 — Continuity-aware Agent Tools](../design/029-agent-filter-context.md).

## Rationale

- **Single source of truth.** The user's filter selection has one channel
  from UI → middleware → tool. The agent isn't a participant; the data layer
  enforces the contract.
- **Generalises.** `realm`, `era`, and any future facet drops in by extending
  the service, not by re-prompting the agent.
- **Cost.** Tools fetch less data when the context is restrictive — fewer
  Mongo round-trips, fewer tokens spent describing irrelevant rows back to
  the agent.
- **Auditability.** A single filter parser feeds OpenTelemetry tags, so traces
  show exactly which filters applied to each tool call.
- **Doesn't preclude explicit overrides.** Tools that genuinely want to span
  continuities (cross-canon comparison questions, etc.) keep their parameter
  and override the ambient default.

## Consequences

- Touches `~5–10` tools that today don't filter at all (`get_galaxy_year`,
  the Mongo MCP read wrappers, possibly some KG aggregations). The KG tools
  that already accept `continuity` only need to default-from-context when the
  parameter is null.
- Adds a small `ICurrentRequestContext` service to `Services.AI` plus
  middleware on the two AGUI endpoints.
- Removes the "pass continuity to tool calls" line from both agents' prompts
  and replaces it with the simpler "the data you receive already matches the
  user's filter; if you need an explicit cross-continuity query, say so."
- Does not affect `/api/*` controllers — those already read filters from
  query strings, which is a fine pattern outside the agent context.

## References

- [Design-022 Page-Aware Copilot Sidebar](../design/022-galaxy-map-copilot.md) — introduced the envelope wire format
- [ADR-002 AI Agent Toolkits](002-ai-agent-toolkits.md) — toolkit organisation
- [Design-029 Continuity-aware Agent Tools](../design/029-agent-filter-context.md) — implementation plan
