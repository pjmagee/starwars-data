# Design-022: Page-Aware Copilot Sidebar

**Status:** Proposal
**Date:** 2026-04-28
**Author:** Patrick Magee + Claude
**Related:** [Design-004 Galaxy Map Architecture](./004-galaxy-map-architecture.md), [Design-006 Galaxy Map Timeline Mode](./006-galaxy-map-timeline-mode.md), [Design-011 Mobile Web UX](./011-mobile-web-ux.md), [ADR-002 AI Agent Toolkits](../adr/002-ai-agent-toolkits.md)

> Earlier draft of this doc proposed a galaxy-map-only "Ask AI" drawer. Replaced
> on 2026-04-28 with the global page-aware sidebar described below — same wire
> protocol, broader surface.

## Problem

Most pages on the Frontend present a clear "thing in focus":

- `/galaxy-map` — a selected region / sector / system / celestial body
- `/timeline` — an active year or era window with a lens filter
- `/graph-explorer` — a focused KG node and its expanded edges
- `/knowledge-graph` — a node selected for inspection
- `/search` — a query string + active filters + a hovered result
- `/character-timelines` — a character whose timeline is being rendered
- `/tables` — an entity type and active column filters

When a user wants to ask the AI a question about that thing — *"who governs this?"*, *"what happened here?"*, *"show me the family tree"* — the only path is to leave the page, open `/ask`, retype the entity name, and hope the agent picks the right context. That's friction users will not pay; the AI capabilities of the site become invisible from anywhere except `/ask`.

The chat agent already has all the tooling needed (GraphRAG, KG analytics, semantic search, MongoDB MCP). It just doesn't know what page the user is on, what's selected, or what filters are active.

## Goals

- A persistent **right-hand sidebar** opposite the existing left nav, hosting an always-available chat surface that follows the user across pages — except `/ask` itself, where it would be redundant.
- A **distinct copilot agent** tuned for the sidebar's job: high-quality flowing prose grounded in semantic search and KG queries, *not* structured visualizations. The user is exploring a dense page; the sidebar's job is to help them read it.
- Each page **publishes its current focus** to a shared service via a small contract; the copilot reads that on submit and injects it as agent context.
- Conversation continuity across page navigation. Switching pages does not throw away the in-progress conversation; the agent simply gets new context on the next turn.
- Narrow shared utilities only — AGUI DTOs and a tiny SSE reader. Two simple components, not one component with two modes.

## Non-goals

- **Replicating `/ask`'s rich-render output in the sidebar.** No `render_chart` / `render_graph` / `render_table` / `render_timeline` / `render_infobox`. The sidebar is text-first by design. If a user wants a chart, they go to `/ask`. Conflating the two surfaces dilutes both.
- **Reusing the `AskAIAgent` directly.** Same toolkit, same prompt, but different optimization targets — the AskAI agent is incentivized to render visualizations end-of-turn (the prompt explicitly suppresses prose after a `render_*` fires; see [Ask.razor:418-420](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor#L418-L420)). That's the wrong shape for a contextual sidebar reader.
- **Showing the sidebar on `/ask`.** Two chat surfaces on the same page is confusing and pointless.
- **Replacing `/ask`.** The full-page chat keeps the empty-state mode cards, dynamic suggestions, structured renders, and dedicated session-history experience. The sidebar is the **quick contextual reader**; `/ask` is the **deep exploration** surface. Different products.
- **Coupling the sidebar to a specific page.** Pages opt in by publishing context; pages that don't publish anything just see a working chat with no context prefix.
- **Persisting sidebar conversations to user history in Phase 1** (optional later — see Open questions).

## Approach

### 1. Layout

Add a second `MudDrawer` to [`MainLayout.razor`](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor#L20), anchored right, symmetric with the existing left nav drawer:

```razor
<MudDrawer @bind-Open="_copilotOpen" Anchor="Anchor.Right"
           ClipMode="DrawerClipMode.Always" Variant="DrawerVariant.Mini"
           OpenMiniOnHover="false" Elevation="2" Width="380px" MiniWidth="48px">
    <CopilotSidebar />
</MudDrawer>
```

- `Variant="Mini"` so collapsed it shows a thin rail with a sparkle icon (`Icons.Material.Filled.AutoAwesome`) — discoverable on every page.
- Toggle button in the `MudAppBar` at the right edge, mirroring the left `Menu` button at line 24.
- Width persisted to localStorage via the existing `swSetUiState` / `swGetUiState` channel used for theme/text-size at [`MainLayout.razor:260`](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor#L260).
- Honors `Layout.IsFullscreen` exactly like the left drawer at [`MainLayout.razor:21`](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor#L21) — galaxy-map fullscreen mode hides both drawers.
- Below `md` (mobile) the sidebar swaps to `Anchor="Anchor.Bottom"` with `Variant="Temporary"` and full width — same gating CSS as Design-011.

### 2. Page-context service

A scoped singleton on the Frontend (one instance per circuit, like `GlobalFilterService`):

```csharp
public sealed record PageContext(
    string  Page,                           // "galaxy-map", "timeline", "graph-explorer", …
    string? Subject,                        // primary entity name in focus
    string? SubjectKind,                    // KG node kind if known
    int?    SubjectId,                      // KG node id if known
    IReadOnlyDictionary<string,string>? Extras  // page-specific facets
);

public sealed class PageContextService
{
    public PageContext? Current { get; private set; }
    public event Action? OnChange;
    public void Set(PageContext ctx);
    public void Clear();
}
```

Pages call `PageContextService.Set(...)` from `OnInitializedAsync`, on relevant state changes, and `Clear()` on `Dispose`. The sidebar subscribes to `OnChange` to update its "current focus" chip. It reads `Current` once **at submit time** to build the agent context prefix — not on every keystroke.

### 3. Wire format (unchanged from earlier draft)

Existing `/ask` already injects per-turn metadata into AGUI user messages by prepending bracketed key=value envelopes — see [Ask.razor:777-779](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor#L777-L779). Reuse that channel:

```text
[CONTINUITY: Canon] [PAGE: galaxy-map] [SUBJECT: CelestialBody #12345 "Balagash"]
  [FACETS: region=Expansion Region;sector=Hali;zoom=system;era=22 BBY;lens=Battles]
  What battles happened here during the Clone Wars?
```

The copilot agent's system prompt teaches it to read these envelopes (full text in the **Server-side changes** section below). The wire format is the same one `/ask` already uses for `[CONTINUITY:][PREFER:]` — the channel is proven and replay-safe.

### 4. Two surfaces, narrow shared utilities

`/ask` and the sidebar are **different products** with different output expectations:

- `/ask` produces structured artifacts (charts, graphs, tables, infoboxes) end-of-turn. Its prompt actively suppresses prose after a `render_*` call fires (see [Ask.razor:418-420](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor#L418-L420)).
- The sidebar produces flowing prose grounded in retrieval. No structured renders.

Trying to express both with one Razor component or one `Func<string,string>` prompt-transform parameter forces conditionals through every render branch and dilutes both UIs. So:

- **Two components.** `Ask.razor` keeps its current architecture untouched. `CopilotSidebar.razor` is a separate ~300-line component with a leaner render switch (text + tool breadcrumb only).
- **Shared at the protocol layer only.** Lift the AGUI DTOs (`AguiMessage`, `AguiToolCall`, `AguiFunction`) and the SSE event-loop helper out of `Ask.razor` into `Components/Shared/Agui/`. Both surfaces import them. Nothing else is shared.

This is the smallest viable shared layer — a few hundred lines of plain types and one helper class. Everything visual stays per-surface where it belongs.

## Per-page context shapes

| Page | `Subject` source | `SubjectKind` | `Extras` |
| --- | --- | --- | --- |
| `/galaxy-map` | `_selectedSystem` or `_detailPage` | `System` / `CelestialBody` / `Region` / `Sector` | `region`, `sector`, `system`, `class`, `zoom`, `era`, `lens` |
| `/timeline` | active era or year window | `Era` or `null` | `from`, `to`, `realm`, `lens` |
| `/timeline-anchored` | anchor entity | KG kind | `from`, `to` |
| `/graph-explorer` | focused node | KG kind | `expandedKinds`, `depth` |
| `/knowledge-graph` | inspected node | KG kind | `tab` (overview / edges / chunks / enrichments) |
| `/search` | query string | `null` | `filters`, `hoveredResultId` |
| `/character-timelines` | character | `Character` | `from`, `to` |
| `/tables` | active entity type | `null` | `entityType`, `filters` |
| `/holocron-*` | inspected job/node | varies | `jobStatus`, `proposalCount` |
| `/`, `/ask`, `/about`, `/profile`, `/privacy`, `/terms` | — | — | (no context published — sidebar acts as a generic chat) |

**Page is required.** `Subject` is optional — a page may publish "you're on /timeline at 22 BBY" with no `Subject` set, and that's a valid context. `Extras` is the escape valve for page-specific fields without bloating the core record.

## UX

- **Trigger:** A persistent right-edge `MudIconButton` (sparkle icon) in the `MudAppBar`, mirroring the left menu button. Optional small `MudBadge` showing "•" when context is freshly available on a page the user just navigated to.
- **Empty state:** "Ask anything about Star Wars" placeholder + 3–4 page-aware suggested prompts when `PageContextService.Current` is set. For `/galaxy-map` selecting Balagash:
  - "What battles happened here?"
  - "Who has governed this world?"
  - "Which characters were born or died here?"
  - "What's the cultural significance of this planet?"
  Suggestions come from a static map keyed on `(page, subjectKind)` — not the dynamic `/api/suggestions` endpoint, which is filter-scoped not page-scoped.
- **Focus chip:** A small chip at the top of the sidebar showing `Page · Subject` (e.g. "Galaxy Map · Balagash"). Click `×` on the chip to suppress context for the next turn (asks the question without the prefix), useful when the user wants to change subject. Suppression resets after the next turn.
- **Page navigation while open:** Conversation stays. The chip swaps. The next turn picks up the new page's context. Never silently throw away conversation, never silently lie about context.
- **Mobile:** Bottom sheet, full-width, `Variant.Temporary`, gated `<md` per Design-011.

## Server-side changes

A new `CopilotAgent` class under [`Services/AI/Agents/`](../../src/StarWarsData.Services/AI/Agents/) and a new endpoint `/copilot/stream` registered in [`ApiService/Program.cs`](../../src/StarWarsData.ApiService/Program.cs). Both follow the same patterns as the existing `AskAIAgent` and `/kernel/stream` — same AGUI message format, same SSE event shape, same auth/rate-limit middleware — so the **wire is identical**, only the agent behind it differs.

### Toolkit

Strict subset of the AskAI agent's toolkit:

- `WikiSearchProvider` — semantic search over the 800K+ article passages.
- `KnowledgeGraphAnalyticsToolkit` — KG node/edge reads, neighbor lookups, hierarchy walks.
- `GraphRAGToolkit` — chunk retrieval scoped to a given node id.
- MongoDB MCP tools (read-only filtered subset — find/aggregate, no writes).

**Excluded by design:**

- All `render_*` tools (chart, graph, table, data_table, timeline, infobox, markdown, aurebesh). The sidebar is a text reader, not a viz host. Excluding the tools is structural — there's no rendering surface in the sidebar for them to target, and dropping them from the registry stops the agent from being tempted to call them.
- `ComponentToolkit` — tied to AskAI's render flow; irrelevant here.

### System prompt

```text
You are the Star Wars Data copilot — a contextual reading assistant that
helps users understand what they're currently looking at on the site.

Output rules:
- Answer in flowing markdown prose. No tables, no charts, no infoboxes.
- Be concise. The user is mid-exploration on another part of the page;
  a six-paragraph essay competes with what they were reading.
- Cite sources inline using [text](wiki-url) markdown links when you reference
  a specific fact retrieved from a tool.
- If a question is ambiguous and the page context narrows it, answer for that
  scope without asking for clarification.

Tooling rules:
- Prefer KG queries when the user asks about relationships, hierarchies, or
  membership. Prefer semantic search when the user asks about motivations,
  themes, or events.
- For deep questions outside what the page context implies, suggest the user
  open the full /ask page rather than producing a long answer here.

Page context:
User messages may include envelopes describing the page the user is on:

  [PAGE: <slug>]                       — required when present
  [SUBJECT: <Kind> #<id> "<Name>"]     — entity in focus, optional
  [FACETS: key=value;key=value;…]      — page-specific filters/state

- Treat SUBJECT as the implicit topic of follow-ups unless redirected.
- Use SUBJECT id to call KG tools directly — do not re-resolve by name.
- FACETS describe the current view (zoom, era, lens, filters). Prefer
  scoped answers when sensible (era-bounded queries when era is set, etc.).
- The envelopes are metadata, not part of the user's question. Do not
  echo them back.
```

Note the explicit "suggest the user open `/ask`" escape hatch — the copilot is allowed to recognize when a question wants the bigger surface and bow out gracefully.

### Two-agent rate-limiting

The existing rate-limit middleware is per-endpoint. Default the copilot to the same per-user budget as `/kernel/stream` for Phase 1 — observe usage, then split the limits if one surface starves the other.

## Frontend wiring

1. **`PageContextService.cs`** — registered scoped singleton on the Frontend, alongside `GlobalFilterService`.
2. **`Components/Shared/Agui/`** — small shared layer holding the AGUI DTOs (`AguiMessage`, `AguiToolCall`, `AguiFunction`) and a `AguiStreamReader` helper that wraps the SSE-line loop. Currently inline at [Ask.razor:367-403](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor#L367-L403) and [Ask.razor:836-919](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor#L836-L919). Lift them out as plain types — no Razor component extraction. Both `Ask.razor` and `CopilotSidebar.razor` import them; neither becomes a thin wrapper around the other.
3. **`CopilotSidebar.razor`** — own component, ~300 lines. Owns its own AGUI message list and SSE loop (using the shared reader). Renders only `User` / `Assistant` / `Error` / lightweight `ToolBreadcrumb` (one-line "🔎 searched the wiki" / "📊 queried the graph" — collapsed by default, expandable for the curious; **no input/output JSON dumps** like `/ask` shows). No visualization branch.
4. **`MainLayout.razor`** — second `MudDrawer` anchored right, app-bar toggle, persisted open-state. Renders nothing when `LayoutService.HideCopilot` is true.
5. **`/ask` opt-out** — `Ask.razor`'s `OnInitialized` sets `Layout.HideCopilot = true`; `Dispose` clears it. Mirrors how `Layout.IsFullscreen` works today at [MainLayout.razor:21](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor#L21).
6. **Page integrations** — each opting page injects `PageContextService` and calls `Set(...)` from lifecycle hooks. Galaxy map ships in Phase 1; the rest land incrementally.

`Ask.razor` keeps its current rich-render architecture untouched — only the AGUI DTOs move to the shared layer.

## Implementation phases

**Phase 1 — minimum viable.**

- Lift AGUI DTOs out of `Ask.razor` into `Components/Shared/Agui/`.
- New `CopilotAgent` class + `/copilot/stream` endpoint (text-only toolkit, copilot system prompt).
- `PageContextService`.
- `CopilotSidebar.razor` (own component, text-first rendering, tool breadcrumbs).
- Right `MudDrawer` in `MainLayout`, app-bar toggle, persisted open-state.
- `Ask.razor` sets `Layout.HideCopilot = true`.
- Galaxy map publishes context.

**Phase 2 — page coverage.** Add `PageContext.Set(...)` calls to timeline, graph-explorer, knowledge-graph, search, character-timelines, tables. Add per-(page, kind) suggested-prompt maps for the empty state.

**Phase 3 — quality of life.**

- Persist sidebar conversations to `chat.sessions` with `source: "copilot-sidebar"` tag, optionally surfaced in `/ask` history with a filter.
- "Continue this in `/ask`" hand-off — copies the in-progress conversation into a fresh `/ask` session and navigates the user there. The natural escape hatch when the copilot's prose answer hits the limits of what text alone can show.

## Open questions

- **Layout cost on dense pages.** A right drawer eats horizontal space on `/galaxy-map`, `/timeline`, `/graph-explorer` — pages that already feel cramped. Mini variant (48px rail) keeps the cost small when collapsed; the user sees the rail and chooses whether to expand. Verify with the dense visual pages before committing the layout.
- **Per-page hide list.** `/ask` definitely hides the sidebar. Should `/profile`, `/privacy`, `/terms`, `/about` also opt out — they have nothing for the copilot to ground on, but a generic chat there is harmless. Default: leave it on; the rail is unobtrusive and consistent.
- **Quota.** Phase 1 shares the `/kernel/stream` budget with `/copilot/stream`. The "always available" framing might burn quota faster — observe before deciding whether to split limits per surface.
- **Privacy of saved context.** If sidebar conversations get persisted in Phase 3, the `[PAGE:][SUBJECT:]` prefix gets stored. Fine for the user's own history; if they share the session URL, it'd leak which entity they were viewing. Probably acceptable; flag for review.
- **Context staleness.** Decision: read `PageContextService.Current` at **submit time**, not at message-compose time. Simpler, predictable.
- **Suggested-prompt source.** Static per-(page, kind) map vs. eventually expanding the `/api/suggestions` endpoint to take a `page=` filter. Static is fine for Phase 1.
- **Hand-off direction.** Phase 3 proposes "continue in `/ask`" as a one-way escape hatch. Going the other way (start in `/ask`, continue in the sidebar) is unnatural — `/ask` already does everything the sidebar does plus more. Skip it.

## Revisit when

**Merge the two agents** if **both**:

1. The two system prompts converge to >80% identical text after Phase 2 page integrations land, AND
2. The render-suppression carve-out for the sidebar can be expressed as a single tool-registry filter at agent-construction time without a separate prompt.

Until then the two agents are intentionally distinct products: `AskAIAgent` is a multimodal explorer that prefers structured renders end-of-turn; `CopilotAgent` is a text-first reader that helps users in-page.

**Drop the page-context-as-prefix wire format** if AGUI gains a first-class `forwardedProps` / state channel that survives replay across the existing client persistence — structured state beats prefix-injected strings at that point.

**Drop the right-drawer layout** if Phase 1 measurement shows the rail consistently hurts the dense pages it's meant to assist (e.g. galaxy map's per-pixel layout becomes intolerable). Fallback: per-page floating action button, lower discoverability but no layout cost.
