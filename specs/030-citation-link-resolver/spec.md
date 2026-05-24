# Design-030: Citation Link Resolver — agent emits IDs, system organises links

**Status:** Implemented (Phases 1–4, 2026-05-18)

> Phase 1 (resolver scaffold + Wiki/Graph/Galaxy-Map *direct* links, the
> `POST /api/citations/resolve` endpoint, `CitationCard.razor`, and
> `CopilotSidebar` regex-extract+resolve wiring) landed in commit 616ae44dd2.
> Phases 2–4 completed 2026-05-18:
>
> - **Phase 2 — indirect spatial linking.** `CitationResolver` runs one bulk
>   `kg.edges` query over `IndirectSpatialLabels` (located_at / homeworld /
>   birthplace / born_on / setting / fought_at + `SpatialEventLabels`,
>   highest-weight edge wins). Battle/Character/Book citations get a Galaxy
>   Map button pointing at their location; **Event-family sources emit the
>   Design-032 `?event=` round-trip**. Reads `ICurrentRequestContext`
>   (Design-029) so a link never points at a continuity-filtered target.
> - **Phase 3 — AskAI reference shape.** `Reference` gained an optional
>   `PageId` (`Title`/`Url` now nullable — backwards-compatible with old
>   persisted sessions and non-KG sources). `ChartToolKit`'s
>   reference-param description and the `AskAIAgent` prompt steer the agent
>   to cite by pageId. `AskReferencesSection` resolves pageId refs into
>   `CitationCard`s and keeps legacy chips for `{title,url}` refs.
>   *(CopilotAgent's inline `[Name](/graph-explorer/{pageId})` prose links
>   are intentionally retained — Design §"Non-goals" keeps inline markdown
>   for fluid reading; CopilotSidebar already resolves those ids into cards.)*
> - **Phase 4 — Timeline + Holocron.** Two bulk PageId probes populate
>   `/character-timelines/{id}` (when a `genai.character_timelines` doc
>   exists) and `/holocron/jobs/{id}` (when a `kg.enrichment_jobs` job
>   exists).
>
> 7 resolver integration tests + 190 unit tests green; verified against
> `starwars-dev`: Yavin 4 (direct), Battle of Yavin →
> `/galaxy-map/499934?event=452305`, Han Solo → Corellia +
> `/character-timelines/452394`. Open questions: inline-markdown-vs-token
> stays "keep inline markdown"; the per-PageId cache stays deferred until
> profiling shows the round trip.

**Date:** 2026-04-30
**Author:** Patrick Magee + Claude
**Related:** [Design-022 Page-Aware Copilot Sidebar](../022-galaxy-map-copilot/spec.md), [Design-029 Continuity-aware Agent Tools](../029-agent-filter-context/spec.md), [ADR-002 AI Agent Toolkits](../../eng/adr/002-ai-agent-toolkits.md)

## Problem

Two agents — `AskAIAgent` and `CopilotAgent` — currently emit citation links
in mutually-incompatible, type-blind ways:

| Agent | What it links | Where it points |
| --- | --- | --- |
| `AskAIAgent` | Every named entity in `references` arrays | Wookieepedia URL only |
| `CopilotAgent` | Every named entity in markdown prose | `/graph-explorer/{pageId}` only |

Neither agent links to the **galaxy map** for spatial entities (planets,
systems, sectors, regions, trade routes), and neither links to the
**knowledge-graph node detail** when relevant. The user has to pick the
"right" view themselves and navigate manually.

Worse, the *agent* is making URL decisions:

- Today's CopilotAgent prompt has a paragraph telling the agent to write
  `[Name](/graph-explorer/{pageId})` and reminding it to "use the exact pageId
  for THAT entity from the tool result". The agent gets this wrong sometimes
  (re-using a pageId across two entities → broken links — observed in
  testing).
- AskAIAgent's render tools take `references[]` arrays where the agent passes
  a `wikiUrl` string. Same fragility — the agent could pass a wiki URL that
  doesn't match the entity it cites.

The right division of labour is: **the agent cites by `pageId`; the system
chooses the destinations**. This design captures that pattern.

## Goals

- Agents pass an **opaque entity reference** (PageId + optional name +
  optional kind hint), not URLs.
- A server-side **citation link resolver** turns the reference into a set of
  navigation options, scoped by the entity's type and relationships.
- Frontend renders the option set as a small **reference component** —
  user picks the surface that suits their question (read on Wookieepedia,
  explore in the graph, locate on the map).
- Same resolver feeds both agents — `AskAIAgent`'s render tools and
  `CopilotAgent`'s inline citations stop diverging.
- Adding a new surface (`/timeline/{pageId}`, `/holocron/jobs/{pageId}`, a
  future `/character-timelines/{pageId}` deep link) is a **resolver change,
  not a prompt change**.

## Non-goals

- Hiding the page-id mechanism entirely from the agent. The agent still
  needs to know IDs come from tool results and that it must not invent them.
- Removing the agent's ability to write inline markdown prose. Inline
  `[Name](url)` links stay for fluid reading; the reference component is for
  end-of-section citations.
- Changing the AGUI streaming protocol — references already flow through the
  existing `references` field on render tool descriptors. We add a structured
  shape, not a new channel.
- Replacing the Wookieepedia link with the in-site link. Both stay — the
  user picks.

## Approach

### 1. Reference shape (server-side DTO)

```csharp
public sealed record CitationReference(
    int       PageId,
    string    Name,
    string    Kind,             // KG node Type, e.g. "Character", "CelestialBody"
    string?   Continuity,       // Canon | Legends | Both | Unknown
    string?   ImageUrl,
    CitationLinks Links);

public sealed record CitationLinks(
    string?   Wiki,             // Wookieepedia URL — virtually always present
    string?   GraphExplorer,    // /graph-explorer/{pageId} — present for every KG node
    string?   GalaxyMap,        // /galaxy-map/{pageId} — only for spatial entities (see below)
    string?   Timeline,         // /character-timelines/{pageId} — only for Characters with timelines
    string?   Holocron);        // /holocron/jobs/{pageId} — only when a Holocron run exists
```

`Links` is the nullable bag. The frontend renders a chip/button per
non-null value, giving the user agency over where to go.

### 2. Galaxy-map availability rule

A node gets a `GalaxyMap` link when it satisfies *either* of:

- **Direct.** The node's `Type` is one of `CelestialBody`, `System`,
  `Sector`, `Region`, `TradeRoute`.
- **Indirect.** The node has at least one outgoing `location` /
  `homeworld` / `birthplace` / `setting` / `fought_at` edge whose target is
  a Direct match. The link points to the *spatial target's* pageId, not the
  source — so the user clicking on a battle's reference sees the battlefield
  on the map, not nothing.

The first KG indirect-edge is the link target. If a battle is fought across
two planets, we pick the canonical one (first `location` edge) and rely on
the agent's prose to mention the second.

This is implemented as a single Mongo `$lookup` on `kg.edges` keyed on a
small static list of "galaxy-map relevant" edge labels — fast enough to run
inside the resolver without caching for the volumes we see.

### 3. Resolver service

```csharp
public interface ICitationResolver
{
    Task<IReadOnlyList<CitationReference>> ResolveAsync(
        IReadOnlyList<int> pageIds,
        CancellationToken ct = default);
}
```

Backed by:

- A single bulk `kg.nodes` `$in` query for the basic fields (Name, Kind,
  Continuity, WikiUrl, ImageUrl).
- A single bulk `kg.edges` `$lookup` for galaxy-map indirect availability.
- A bulk `kg.enrichments` / `genai.holocron_jobs` lookup for the Holocron
  flag (cheap — keyed on PageId).

One round trip per resolve call regardless of how many ids the agent passes.
Resolver is **scoped** in DI; safe to inject anywhere a tool runs.

### 4. Agent contract simplification

Both agents' render-tool / prose-tool prompts collapse from
*"emit `[Name](/graph-explorer/{pageId})` for every entity, never reuse
pageIds, etc."* to:

> *To cite an entity, attach its `pageId` to the call. The system organises
> the user-visible links — you don't need to know the URL shape. Never
> invent a pageId; if you don't have one in tool results, the entity is
> uncitable.*

The render tools still accept inline markdown for prose; entity citations
within prose can either stay as `[Name](/graph-explorer/{pageId})` (fine,
opens a single surface in a new tab) or move to a special token the
frontend turns into a reference chip (`{{ref:12345}}`). Pick during
implementation; both work.

### 5. Frontend reference component

A new `CitationCard.razor` (or extend `AskReferencesSection`) renders one
card per `CitationReference` with:

- Entity name + kind chip + continuity badge
- Optional thumbnail (from `ImageUrl`)
- One link button per non-null entry in `Links`, in a fixed display order:
  Galaxy Map (when present, lead with it for spatial entities) → Knowledge
  Graph → Character Timeline → Holocron → Wookieepedia.
- All links open in new tabs (the existing `swOpenLinksInNewTab` helper
  already handles this for the copilot drawer; we extend it to cover the
  reference cards in `/ask` too).

Mobile fallback: the card collapses to a single dropdown menu with the same
options.

### 6. Backwards compatibility

The existing `references` arrays on render tool descriptors are
`{ title, url }` pairs. We extend the schema to accept either:

- legacy `{ title, url }` — kept working, renders one Wookieepedia button
- new `{ pageId }` — resolved server-side into `CitationReference`

Old chat sessions persisted in `chat.sessions` keep rendering correctly with
the legacy shape. New sessions emit the new shape once the agent prompt is
updated.

## Implementation phases

**Phase 1 — Resolver + the basic three links.**

- New `CitationReference` / `CitationLinks` DTOs in `Models`.
- New `ICitationResolver` + Mongo implementation in `Services.AI`.
- Galaxy-map availability rule (direct types only — defer indirect to phase 2).
- Frontend `CitationCard.razor` rendering Wiki + Graph Explorer + Galaxy Map
  buttons.
- Wire CopilotAgent's inline link rule to read from resolver via a small
  helper. Prompt simplifies.

**Phase 2 — Indirect spatial linking.**

- Resolver runs the `$lookup` for indirect spatial edges.
- Battles, characters, organisations gain Galaxy Map links pointing to their
  associated location.

**Phase 3 — AskAI render tool migration.**

- Render-tool descriptors gain the `{ pageId }` reference shape alongside
  the legacy `{ title, url }` shape.
- AskAI's system prompt swaps `references` examples to use the new shape.
- Old persisted sessions keep rendering (backwards-compatible).

**Phase 4 — Add Timeline + Holocron surfaces.**

- `/character-timelines/{pageId}` link for Characters that have a
  CharacterTimeline document.
- `/holocron/jobs/{pageId}` link when a Holocron job exists for that node.

## Open questions

- **Inline markdown vs. token replacement.** Agent currently writes
  `[Name](/graph-explorer/{pageId})` in prose. With a resolver, an inline
  reference could become `{{ref:12345}}` and the frontend swaps in a small
  inline button-pill. Cleaner, but may interact awkwardly with markdown
  rendering. Decision deferred to implementation; default is keep inline
  markdown for prose, use `CitationCard` for end-of-section citations.
- **Cache invalidation.** A node's link set changes when the KG is rebuilt
  (Phase 1 ETL) or a Holocron job completes. The resolver runs on every
  request — no caching, simple. If it shows up in profiling, cache by
  PageId for 5 minutes with a Hangfire callback to invalidate on rebuild.
- **What happens for nodes with no KG record?** Resolver returns a minimal
  `CitationReference` with only the Wiki link populated. Frontend renders
  the single-button case identically; no special path.
- **Continuity filtering on indirect edges.** When a Canon-only user views
  a Canon battle whose only location is Legends-tagged, do we suppress the
  galaxy-map link? Probably yes (the link would lead to a node hidden by the
  user's filter). Cheap to implement once Design-029's request-scoped
  context lands — resolver reads `ICurrentRequestContext.Continuity` and
  filters indirect targets accordingly.
- **`AskReferencesSection` styling.** Today it's a row of small wiki-link
  chips. New cards are larger. Either redesign that section to use cards,
  or keep both: cards for first-class entities, chip row for raw URL refs.

## Revisit when

- A new in-site surface gets enough usage that we want the agent to cite to
  it (e.g. a hypothetical `/research/{pageId}` for AI-generated research
  papers). Resolver grows by one `Links` field; no agent change.
- Microsoft.Agents.AI ships first-class structured-citation support that
  supersedes our render-tool `references[]` channel. Swap the wire format,
  keep the resolver and component shape.
- Volume hits a level where the Mongo round trip per request becomes
  visible — at that point, add the per-PageId cache mentioned above.

## Why this is better than agent-side URL emission

- **One source of truth for link shape.** Adding `/timeline/{pageId}` is a
  resolver change; no prompt edits, no risk of agents lagging.
- **Agent doesn't need to know URL formats.** Eliminates a class of bugs
  (broken/wrong-entity links) that currently exists despite explicit prompt
  rules.
- **User agency.** A planet citation gets three buttons (map, graph, wiki);
  the user picks the surface that suits their question instead of getting
  whatever the agent guessed.
- **Type-awareness without type-knowledge in the agent.** A character cites
  identically to a planet from the agent's POV; the resolver decides one gets
  a galaxy-map button and the other doesn't.
- **Pairs naturally with Design-029.** The same request-scoped context that
  Design-029 introduces for filter awareness is what lets the resolver
  suppress links to filtered-out targets.
