# Design-029: Continuity-aware Agent Tools

**Status:** Partially shipped (2026-05-18, commit 616ae44dd2) — Phase 1 landed: `ICurrentRequestContext`/`CurrentRequestContext` (`Services/AI/RequestContext/`), the `AguiEnvelopeParserMiddleware` wired on the streaming endpoints in `ApiService/Program.cs`, and `GraphRAGToolkit` defaults `continuity` from context across `search_entities` / `find_entities_by_year` / `get_entity_relationships` / graph-query / lineage / connections and filters `get_galaxy_year`'s `EventCells`/`UnresolvedEvents` by context. Phases 2 (wiki-search post-filter), 3 (Mongo MCP continuity wrapper) and 4 (AskAI agent) are not yet implemented.
**Date:** 2026-04-30
**Author:** Patrick Magee + Claude
**Related:** [ADR-008 Agent request-scoped filter context](../adr/008-agent-request-scoped-filter-context.md), [Design-022 Page-Aware Copilot Sidebar](022-galaxy-map-copilot.md), [ADR-002 AI Agent Toolkits](../adr/002-ai-agent-toolkits.md)

## Problem

When the user has the global continuity toggle set to *Canon only*, both AI
agents (`AskAIAgent`, `CopilotAgent`) can still mention Legends-only entities
in their answers. Concrete failure observed during copilot testing on the
galaxy map at year −100,000 BBY with Legends off:

> *"In Canon, the visible entries are concentrated around Coruscant… The rest
> of the activity on this snapshot is largely Legends-only, including things
> like Abeloth, Daughter/Legends, Son/Legends…"*

The map's visual layer was fixed in the same session by client-side filtering
of `_yearData.EventCells` before the JS render. The agent's leak is a
different bug at a different layer:

- The frontend injects `[CONTINUITY: Canon]` into the user's message envelope.
- The agent's system prompt instructs it to *pass continuity to tool calls*.
- But several tools have no `continuity` parameter (`get_galaxy_year`, Mongo
  MCP read tools, some aggregations). They return everything regardless.
- The agent then composes an answer over mixed data, sometimes leaking
  filtered-out entries into the prose even when the prompt tells it not to.

ADR-008 documents the architectural decision: **tools should read filters
from a request-scoped ambient context**, not require the agent to pass them
as parameters. This design is the implementation plan for that decision.

## Goals

- A single `ICurrentRequestContext` exposes `Continuity` and `Realm` for the
  duration of one `/kernel/stream` or `/copilot/stream` HTTP request.
- Every tool that touches continuity-tagged data reads from the context as a
  default. The agent doesn't see filtered-out content.
- Pattern generalises to future facets (era, lens, custom filters) without
  reshaping the toolkit.
- Defence-in-depth: keep a small prompt rule as fallback for tools we don't
  wire (e.g. third-party MCP tools we don't control).

## Non-goals

- Changing how `/api/*` controllers handle filters. They read query strings;
  that pattern is fine.
- Re-architecting the toolkit organisation (ADR-002 stays).
- Moving filters out of the message envelope. The envelope IS the wire format;
  this design just adds a server-side parser that pins the values on a
  request-scoped service so tools can read them.
- Filtering the agent's *output* rendering (markdown, render_*). That's still
  the agent's responsibility.

## Approach

### 1. Service contract

```csharp
public interface ICurrentRequestContext
{
    Continuity? Continuity { get; }   // null = "Both" / no filter
    Realm?      Realm      { get; }   // null = both Star Wars + Real World
    string?     Page       { get; }   // for tools that want to be page-aware
    int?        SubjectId  { get; }   // when [SUBJECT: ... #id ...] is present

    // Future: Era, Lens, custom facets — additive, never breaking.
}

public sealed class CurrentRequestContext : ICurrentRequestContext
{
    public Continuity? Continuity { get; init; }
    public Realm?      Realm      { get; init; }
    public string?     Page       { get; init; }
    public int?        SubjectId  { get; init; }
}
```

Registered as **scoped** in DI on the ApiService. One instance per HTTP
request, populated by middleware before the agent runs, read by tools during
execution.

### 2. Envelope parser middleware

A small middleware on `/kernel/stream` and `/copilot/stream` parses the
incoming AGUI request body, extracts the most recent user message, and runs
the existing bracketed-envelope regex against it:

```csharp
app.Use(async (context, next) =>
{
    if (IsAguiPost(context))
    {
        var ctx = await ParseEnvelopeAsync(context.Request);
        var current = context.RequestServices.GetRequiredService<CurrentRequestContext>();
        current.Continuity = ctx.Continuity;
        current.Realm      = ctx.Realm;
        current.Page       = ctx.Page;
        current.SubjectId  = ctx.SubjectId;
    }
    await next();
});
```

The parser is the same regex-set the agent's prompt instructs it to read,
just executed server-side. Body must be re-buffered for the AGUI handler;
this is one `Request.EnableBuffering()` call.

### 3. Tool wiring — by category

| Tool family | Today | After |
| --- | --- | --- |
| KG queries with explicit `continuity` param (`search_entities`, `find_entities_by_year`, `get_entity_relationships`, all of `KGAnalyticsToolkit`) | Agent must pass; sometimes forgets | Default from context when param is null |
| `get_galaxy_year` (GraphRAG) | Returns full year doc, no filter | Filter `EventCells` + `UnresolvedEvents` server-side using context.Continuity before serialising |
| `get_entity_properties`, `get_entity_timeline` | Don't filter (entity is already pinned) | No change — id-targeted queries don't need continuity |
| `semantic_search`, `keyword_search` (wiki) | No continuity awareness | Add post-filter: drop chunks whose source page's continuity doesn't match. Cheap because Mongo Atlas Search returns the page id; one bulk lookup of `kg.nodes.continuity` per result set |
| Mongo MCP tools (`find`, `aggregate`, `count`) | External; can't change signature | Wrap at registration time — `CopilotAgent`/`AskAIAgent` build wrappers that inject a continuity match stage into the pipeline before forwarding to MCP |
| `render_*` (AskAI only) | Frontend renders what it's given | No change — agent's responsibility to pass filtered data into the descriptor |

Estimated: 5–8 tool implementations touched, plus one wrapper for the MCP
forwarder. Most are 2–4 line changes (read context, default param, forward).

### 4. Prompt simplification

Both agents' system prompts replace the current language:

> *"Pass continuity to tool calls: Canon, Legends, or omit for Both."*

with:

> *"The data you receive from tools already matches the user's active
> continuity / realm filter. Do not reason about filtered-out content; do
> not tell the user you filtered something. If you need an explicit
> cross-continuity query (e.g. 'compare Canon and Legends versions of X'),
> pass `continuity='Both'` to override the default."*

The "drop silently" hard rule from the stop-gap commit stays as a fallback
for any unwired tool.

### 5. Realm gets the same treatment for free

Realm filtering today happens client-side via the lens IsReal flag (see
`GalaxyMapUnified.BuildFilteredYearData`). The same pattern applies — service
exposes `Realm`, tools that return real-world content (publication-year
events, Real Life lenses) filter via context.

## Implementation phases

**Phase 1 — Service + middleware + the worst offenders.**

- New `ICurrentRequestContext` + `CurrentRequestContext` in
  `StarWarsData.Services/AI/RequestContext/`.
- Envelope parser middleware on both streaming endpoints.
- Wire `get_galaxy_year` (GraphRAG) to filter against context — the tool that
  produced the originally-reported leak.
- Wire `find_entities_by_year` and `find_entities_by_property` to default
  `continuity` from context.
- Update `CopilotAgent` prompt to drop the "pass continuity" line.

**Phase 2 — Wiki search.**

- Post-filter `semantic_search` / `keyword_search` results by source-page
  continuity. Single bulk `kg.nodes.continuity` lookup per result set.

**Phase 3 — Mongo MCP wrapper.**

- Build a thin wrapper around the MCP `find` / `aggregate` tools that injects
  a `{ continuity: { $in: [...] } }` match stage into pipelines that target
  collections with a continuity field (`kg.nodes`, `kg.edges`, `raw.pages`).
- Ship behind a feature flag at first; observe traces, then remove flag.

**Phase 4 — AskAI agent.**

- Same wiring on `AskAIAgent`. Postponed to last because it has render_* tools
  that may need different rules — e.g. a Pie chart of "Battles by Era" that
  spans both continuities is a legitimate explicit-Both query.

## Testability

The biggest concern about this design is that it appears to couple tool
implementations to ASP.NET request plumbing. It doesn't have to — and we
should make sure the test surface stays the same shape it has today.

### Default behaviour when there is no HTTP request

`CurrentRequestContext` is a plain mutable record registered scoped in DI.
When no HTTP request is active (background jobs, integration test fixtures
that instantiate tools directly, the test runner itself) it stays at its
default — all properties null. That maps to "no filter / `Both` / unknown
page" — which is the **current behaviour** of every tool today. So:

- **Existing unit + integration tests pass unchanged.** They never set the
  context, the service returns null, tools behave as today.
- **Hangfire background jobs** (CharacterTimelines generation, Holocron
  workflow batches, ETL pipeline tasks) inherit the same null-context
  default. They already decide their own continuity scope per-input; nothing
  to change there.
- **Microsoft.Agents.AI workflow runs** that call tools outside an HTTP
  request (Holocron consolidator, etc.) get null context, behave as today.

The middleware in section 2 is the *only* place that populates the context.
Everything else is opt-in.

### Verifying continuity-aware behaviour in tests

Tests that *want* to verify a tool filters correctly under a given continuity
register their own context up front. With xUnit / MSTest fixture pattern
already used by `ApiFixture`:

```csharp
[TestMethod]
public async Task GetGalaxyYear_FiltersToCanon_WhenContextIsCanon()
{
    using var scope = ApiFixture.Services.CreateScope();
    var ctx = scope.ServiceProvider.GetRequiredService<CurrentRequestContext>();
    ctx.Continuity = Continuity.Canon;

    var tool = scope.ServiceProvider.GetRequiredService<GraphRAGToolkit>();
    var result = await tool.GetGalaxyYear(year: -100_000, ct: TestContext.CancellationTokenSource.Token);

    Assert.IsFalse(result.EventCells.Any(c => c.Events.Any(e => e.Continuity == Continuity.Legends)));
}
```

A small `WithFilters(Continuity?, Realm?)` helper extension on the fixture
service provider keeps test code terse:

```csharp
using var scope = ApiFixture.WithFilters(continuity: Continuity.Canon);
```

### Agent-tier tests (live OpenAI + real Mongo)

The existing `AskAiPipelineTests`, `RelationshipQueryTests`, `TemporalQueryTests`
in the Agent tier exercise the agent end-to-end against `/kernel/stream`
(or its in-process equivalent through `WebApplicationFactory`). Since they
go through real HTTP, the middleware fires and populates the context from
the envelope — same path as production. **No test changes required** as long
as the test prompts include `[CONTINUITY: …]` like the frontend does (which
they already do, via the same envelope format).

### Tools that don't run inside DI

A small subset of tools (Mongo MCP read wrappers) are forwarded from an
external MCP server. The wrapper that injects the continuity match stage
(Phase 3 of the rollout) lives inside our DI; the external MCP itself is
unchanged. Tests targeting the wrapper get the same null-default behaviour
when no context is set.

### Net testability summary

| Test tier | Today | After |
| --- | --- | --- |
| Unit (`Unit/`) — pure logic, no HTTP | Pass | Pass — context is null, tools behave as today |
| Integration (`Integration/`) — Testcontainers Mongo, no HTTP | Pass | Pass — same null-context path |
| Integration tests that *want* to verify filter behaviour | n/a | Add 1-line `ctx.Continuity = …` setup; ~5 new tests total |
| Agent (`Agent/`) — live OpenAI, real HTTP | Pass | Pass — middleware fires, envelope populates context |

The design intentionally keeps the test seam at the same level it is today:
construct tool, invoke method, assert. The new dependency (`ICurrentRequestContext`)
is null-safe by default and only matters when you explicitly set it.

## Open questions

- **Does the agent ever legitimately need to see filtered-out content?**
  Probably yes — *"compare the Canon and Legends versions of Anakin's death"*
  is a meaningful question. The override is `continuity='Both'` passed
  explicitly, and the prompt teaches the agent to pass it when the user's
  question crosses continuities. We rely on the agent to recognise this; if
  it gets it wrong (always asks for Both, defeating the filter), tighten the
  prompt back up.
- **Background runs (CharacterTimelines, Holocron) don't have a user request
  context.** They run in Hangfire jobs. Decision: ambient context defaults to
  `Both` / no filter when no HTTP request is active. Background jobs already
  decide their own continuity scope per-input.
- **What about controllers that proxy to the agent?** Today all agent traffic
  flows through `/kernel/stream` and `/copilot/stream`. If we add other entry
  points later, the middleware list expands.
- **Should `Page` and `SubjectId` move out of the prompt entirely** and just
  be ambient context that tools read? Probably yes for `SubjectId` — many
  tools take a PageId as their first arg and the agent could just default
  from context. Save for Phase 5.

## Revisit when

- A new filter facet (era, lens) gets enough usage that we want it in the
  agent's view of the world. The existing service grows by one property; the
  middleware grows by one regex match.
- Microsoft.Agents.AI publishes first-class request-context plumbing that
  supersedes our middleware (currently they expose tool-level state but not
  per-request scoped DI binding). At that point, swap implementation, keep
  the `ICurrentRequestContext` interface stable.
- Background-job toolkits (CharacterTimelines, Holocron) start needing the
  same filter awareness. Today they don't; their inputs are pre-scoped.
