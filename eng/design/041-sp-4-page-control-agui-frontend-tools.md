# Design-041: SP-4 Page Control via AGUI Frontend Tools

**Status:** Proposed
**Date:** 2026-05-21
**Author:** Patrick Magee + Claude
**Related:** [Design-022 Page-Aware Copilot Sidebar](./022-galaxy-map-copilot.md), [Design-004 Galaxy Map Architecture](./004-galaxy-map-architecture.md), [Design-006 Galaxy Map Timeline Mode](./006-galaxy-map-timeline-mode.md), [Design-031 Galaxy Map Deep-Link Route](./031-galaxy-map-deep-link-route.md), [Design-032 Galaxy Map Events at Location](./032-galaxy-map-events-at-location.md), [Design-034 Galaxy Map Temporal](./034-galaxy-map-temporal.md), [Design-029 Agent Filter Context](./029-agent-filter-context.md)

## Problem

SP-4 (the [CopilotSidebar](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor) introduced in [Design-022](./022-galaxy-map-copilot.md)) currently reads the page — `PageContextService` publishes `[PAGE:][SUBJECT:][FACETS:]` envelopes, the agent grounds its prose in that — but it cannot **drive** the page. On `/galaxy-map`, if a user types "show me Coruscant" or "highlight the Outer Rim battles during the Clone Wars", SP-4's only option is to write a markdown answer with `[Coruscant](/graph-explorer/12345)` links that yank the user off the map. There is no path for the agent to call `drillToDeepLink({ kind: "System", pageId: 12345 })` or `HighlightSector("Bothan Sector")` on the user's behalf.

Two concrete consequences:

1. **The map's interactive verbs are invisible to the AI.** [GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor) already exposes a rich command surface: `drillToDeepLink({kind, name, pageId})` in [js/galaxy-map-unified.js](../../src/StarWarsData.Frontend/wwwroot/js/galaxy-map-unified.js); JSInvokable `OnSystemSelected` / `OnCelestialBodySelected` / `OnRegionHovered`; component methods `GoBack`, `GoToOverview`, `ToggleFullscreen`, `ToggleDetailPanel`, `OnModeChanged("explore"|"timeline")`, `ToggleRegion`, `HighlightSector`, `LoadPageDetail`, `ShowAllRegions/HideAllRegions`, `OnSemanticToggle`, era/lens setters (Design-034). The agent has read access to *what the user is looking at* (FACETS) and write access to *nothing*.
2. **Frontend's AGUI client is hand-rolled and not extensible.** [AguiSseReader.cs](../../src/StarWarsData.Frontend/Components/Shared/Agui/AguiSseReader.cs) is a 30-line `data:`-line loop; [CopilotSidebar.razor](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor) hand-dispatches `TOOL_CALL_START` / `TOOL_CALL_ARGS` / `TOOL_CALL_END` / `TOOL_CALL_RESULT` / `TEXT_MESSAGE_*` / `RUN_ERROR`. It works for the read-only sidebar but has no plumbing for **client-side tool execution** — the AGUI primitive where the server emits a `TOOL_CALL_*` whose execution lives in the browser, not on the server. The server side already uses the official package (`builder.Services.AddAGUI()` + `app.MapAGUI("/copilot/stream", …)` in [ApiService/Program.cs](../../src/StarWarsData.ApiService/Program.cs#L31-L252) via `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`); the Frontend does not.

Result: a copilot called "SP-4" — modelled on an *analysis droid* — that can read your maps but cannot operate them.

## Goals

- A user can ask SP-4 in plain English on `/galaxy-map` to **navigate** ("take me to Coruscant", "zoom out to the Outer Rim"), **highlight** ("highlight Hutt Space", "show me the Hydian Way"), **filter** ("hide Wild Space regions", "switch to Timeline mode and play from 22 BBY"), and **inspect** ("open the details panel for the Death Star"), and SP-4 executes the verb on the page instead of (or in addition to) replying in prose.
- A single shared client-side **action contract** the focused page registers handlers on. Pages opt in by registering; pages that don't opt in degrade gracefully — SP-4 explains it can't take that action here.
- Honest separation between **reading** the page (Design-022 envelope, unchanged) and **driving** it (this design). SP-4 always narrates what it just did in one short sentence so the user is never surprised by a viewport change they didn't initiate.
- Adopt the official [`Microsoft.Agents.AI.AGUI`](https://www.nuget.org/packages/Microsoft.Agents.AI.AGUI) client on the Frontend so the SSE event loop, frontend-tool dispatch, and state-channel handling stop being our problem to maintain. Retire the hand-rolled `AguiSseReader` and the inline `TOOL_CALL_*` switch in `CopilotSidebar.razor`.
- Galaxy map ships in Phase 2. The contract is page-agnostic; timeline / graph-explorer / knowledge-graph adopt it in later phases without a Frontend rewrite.

## Non-goals

- **Letting SP-4 take destructive or persistent actions.** No edits to KG data, no enrichment submissions, no admin actions, no Holocron triggers. The verb set is strictly **navigation, highlighting, filtering, and panel toggles** — UI state only. If a user asks for a destructive action the tool registry simply does not contain it and SP-4 falls back to prose.
- **Removing the prose reply.** SP-4 still answers; the page-control tools augment the answer, they don't replace it. The sidebar remains a text reader (Design-022 non-goal — "no render_*" — unchanged).
- **Letting SP-4 control pages it isn't on.** The action registry is scoped to the *currently focused page* (the page that called `PageControlService.Register(...)` in `OnInitialized`). If the user navigates away mid-turn, the in-flight tool call returns "page no longer available" and SP-4 narrates it.
- **A second sidebar agent for `/ask`.** `/ask` continues to be visualization-first per Design-022. Frontend tools are SP-4's value-add precisely *because* the sidebar can't render charts.
- **Re-architecting `Ask.razor`.** Adopting `AGUIChatClient` in Phase 1 covers the sidebar only. `Ask.razor` keeps its hand-rolled loop until/unless the migration proves stable on the smaller surface (Open questions).
- **Putting SP-4 on a different `MapAGUI` endpoint per page.** One agent, one endpoint, one prompt. The available tool set varies per turn via the AGUI `RunAgentInput.tools` channel (see Design § 3).

## Design

### 1. The AGUI frontend-tool primitive

AGUI ships three primitives the protocol uses to push state from the agent into the UI:

- **`TOOL_CALL_*` events with a client-side executor.** The server-side agent's tool catalog includes tool *declarations* (name + JSON-Schema parameters) that have **no server implementation**. When the model emits one, the server dispatches the usual `TOOL_CALL_START` / `TOOL_CALL_ARGS` / `TOOL_CALL_END` SSE events but does not execute the call — instead it waits for the client to send back a `role: "tool"` message with the result on the next `RunAgent` turn. The client sees a tool name it recognises as local, executes it in the browser, and posts the result. The wire shape is identical to a server-side tool call; only the executor location differs.
- **`STATE_DELTA` / `STATE_SNAPSHOT`.** A JSON-Patch channel for shared agent ↔ UI state. The agent can write into a shared document and the UI binds to it.
- **`CUSTOM_EVENT`.** An app-defined event the agent can emit; the client switches on `name`. The escape hatch for events that don't fit either of the above.

For *imperative page actions* — "navigate", "highlight", "toggle" — **frontend tools are the correct primitive**, not `STATE_DELTA`:

- Each action is a discrete verb with named parameters and a discoverable schema the model can read from the tool catalog, exactly how it reads server-tool schemas today.
- The tool call has a unique `toolCallId` and a return value; SP-4 can branch on success/failure ("I couldn't find a system matching 'Bothan' — the closest is Bothawui in the Both system; want me to go there instead?").
- It composes with the existing `[CONTINUITY:][PAGE:][SUBJECT:][FACETS:]` envelope without inventing a new channel.

`STATE_DELTA` is the right fit for *declarative shared state* — a filter dictionary, a selection set, a list — where the agent wants to be the source of truth for some slice of UI state. Most galaxy-map actions are not that shape (navigating to a system is one-shot, not "agent now owns the selected-system slot"). `STATE_DELTA` is on the table for a later phase if a use case (e.g. SP-4 owning a "watch list" panel of highlighted systems across turns) appears.

### 2. Adopt `Microsoft.Agents.AI.AGUI` on the Frontend (Phase 1)

The Frontend takes a `PackageReference` on `Microsoft.Agents.AI.AGUI` 1.6.1-preview (matching the version the API service already pulls transitively via `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`). The package provides an `AGUIChatClient` that wraps the SSE round-trip:

```csharp
// Program.cs
builder.Services.AddHttpClient<AGUIChatClient>("StarWarsData.Copilot", c =>
{
    // Same handler chain as the existing "StarWarsData" client — propagates the
    // X-User-Id header set by the auth DelegatingHandler (ADR-001).
    c.BaseAddress = new Uri("https+http://apiservice");
});
```

`CopilotSidebar.razor` is rewritten against `AGUIChatClient`'s message loop. The hand-rolled `AguiSseReader` is deleted; the inline `switch (eventType)` block on lines 392–437 of `CopilotSidebar.razor` collapses into client-tool handler registrations + a single `await foreach` over the typed event stream.

The shared `AguiMessage` / `AguiToolCall` / `AguiFunction` DTOs in `Components/Shared/Agui/` become redundant once both surfaces use the package's typed messages; they stay in place until `Ask.razor` is migrated (see *Open questions*), then deleted.

> **Phase 0 spike.** The `Microsoft.Agents.AI.AGUI` 1.6.1-preview NuGet page does not enumerate types and the linked sample ([Generative-AI-for-beginners-dotnet/samples/AgentFx/AgentFx-AIWebChatApp-AG-UI](https://github.com/microsoft/Generative-AI-for-beginners-dotnet/tree/main/samples/AgentFx/AgentFx-AIWebChatApp-AG-UI)) demonstrates the client constructor but not the frontend-tool registration API. Before Phase 1 starts, a half-day spike confirms: (a) the .NET client exposes a frontend-tool registration hook (handler keyed on tool name), (b) the wire shape matches what the server-side `MapAGUI` accepts when forwarding `tools` in `RunAgentInput`, (c) cancellation, error mapping, and the `[CONTINUITY:]` envelope pre-pend still work. If the .NET client does NOT yet support frontend-tool dispatch in 1.6.1-preview, fall back to keeping the hand-rolled reader and extending it (Alternatives § A). Either way, Phase 2's contract is unaffected.

### 3. `PageControlService` — the action contract

A new scoped singleton on the Frontend, sibling to `PageContextService`:

```csharp
namespace StarWarsData.Frontend.Services;

public sealed record PageAction(string Name, string Description, JsonElement ParametersSchema);

public sealed record PageActionResult(bool Ok, string? Message = null, JsonElement? Data = default);

public sealed class PageControlService
{
    // Tools the focused page advertises for SP-4's next turn. Empty if no page registered.
    public IReadOnlyList<PageAction> AvailableActions { get; }

    // Page registers handlers in OnInitialized; unregisters in Dispose.
    public IDisposable Register(string page, IReadOnlyList<PageAction> actions, Func<string, JsonElement, CancellationToken, Task<PageActionResult>> dispatch);

    // Invoked by CopilotSidebar when AGUIChatClient routes a frontend-tool call.
    public Task<PageActionResult> InvokeAsync(string name, JsonElement args, CancellationToken ct);

    public event Action? OnChange;
}
```

Rules:

- Only **one** page can be registered at a time (the focused page). `Register` returns an `IDisposable` whose `Dispose` removes the registration; pages call it from `IDisposable.Dispose` / `IAsyncDisposable.DisposeAsync`. A second `Register` while one is active is a programmer bug and throws — never silently overwrite.
- The handler `Func` runs on the Blazor circuit's sync context (callers wrap in `InvokeAsync` themselves, like every other UI-touching service in this repo).
- `PageControlService.AvailableActions` is consumed by `CopilotSidebar.SubmitAsync` at submit time and forwarded as the `tools` field of the AGUI `RunAgentInput`. This is the key: **the tool catalog SP-4 sees this turn is the union of the server-side toolkit and whatever the focused page advertises**. Switch to `/timeline` and SP-4 loses `galaxy_map_navigate` and gains `timeline_set_year`. No prompt rewrite, no second agent.

### 4. Galaxy map's action set (Phase 2)

`GalaxyMapUnified.razor` registers in `OnInitialized` (alongside its existing `PageContext.Set(...)`). Each action is a thin wrapper over a method already in the component:

| Tool name | Args | Maps to |
| --- | --- | --- |
| `galaxy_map_navigate` | `{ pageId: int, kind: "System"\|"CelestialBody"\|"Sector"\|"Region"\|"TradeRoute", name?: string }` | `_module.InvokeAsync<bool>("drillToDeepLink", …)` — same path the `/galaxy-map/{PageId}` deep link uses (Design-031). |
| `galaxy_map_go_overview` | `{}` | `GoToOverview()` |
| `galaxy_map_go_back` | `{}` | `GoBack()` |
| `galaxy_map_set_mode` | `{ mode: "explore"\|"timeline" }` | `OnModeChanged(mode)` |
| `galaxy_map_toggle_region` | `{ region: string, visible: bool }` | `ToggleRegion(region)` (idempotent on desired state) |
| `galaxy_map_show_all_regions` | `{}` / `{}` | `ShowAllRegions()` / `HideAllRegions()` |
| `galaxy_map_highlight_sector` | `{ sector: string }` | `HighlightSector(sector)` |
| `galaxy_map_open_details` | `{ pageId: int }` | `LoadPageDetail(pageId)` + `_detailPanelOpen = true` |
| `galaxy_map_close_details` | `{}` | `_detailPanelOpen = false` |
| `galaxy_map_toggle_fullscreen` | `{ fullscreen?: bool }` | `Layout.IsFullscreen = …` (toggle if arg omitted) |
| `galaxy_map_set_era` | `{ era?: string, year?: int }` | `SetEra(...)` / `SetYear(...)` (Design-034) |
| `galaxy_map_set_lens` | `{ lens: "Battles"\|"Politics"\|"Trade"\|… }` | active-lens setter |
| `galaxy_map_play_timeline` | `{ playing: bool, fromYear?: int }` | `_autoplay` toggle + start year |

Every handler returns `PageActionResult.Ok(message)` with a one-line confirmation (`"Centred on Coruscant (system, Core Worlds)."`) the agent can quote in its prose. Failures return `Ok=false` with a reason the agent can act on (`"No system named 'Bothan' — did you mean Bothawui?"`). The agent is instructed to always narrate one short sentence after a successful action so the user is never surprised by a viewport change.

> Naming convention: `<page>_<verb>[_<object>]`, snake_case. The `<page>` prefix is mandatory — when the action registry composes with the server-side tools, the agent has to be able to tell at a glance whether a call drives the page or queries the data layer. `galaxy_map_navigate` and `get_entity_properties` should never be confusable.

### 5. Wire path end-to-end

1. User types "take me to Coruscant" in SP-4 with `/galaxy-map` focused.
2. `CopilotSidebar.SubmitAsync` builds the AGUI `RunAgentInput`:
   - `messages`: prior conversation + the new user message (envelope-prefixed as today).
   - `tools`: `[…server-toolkit (static, defined by CopilotAgent), …PageControlService.AvailableActions]`.
3. `AGUIChatClient` POSTs to `/copilot/stream`. Server adds the page tools to the model's tool catalog for this turn only.
4. Model emits `TOOL_CALL_START { toolCallName: "galaxy_map_navigate" }` + args. Server has no executor registered for that name, so it forwards the events to the SSE stream and pauses, waiting for the client tool result.
5. `AGUIChatClient`'s frontend-tool handler for `galaxy_map_navigate` calls `PageControlService.InvokeAsync(...)`, which calls `GalaxyMapUnified`'s registered dispatch, which calls `drillToDeepLink(...)`.
6. Client sends back a `role: "tool"` message with the `PageActionResult` JSON.
7. Server resumes; model emits `TEXT_MESSAGE_*` ("Centred on Coruscant — the Core Worlds capital. Want me to highlight the surrounding sectors?").
8. Sidebar renders the prose; the map already moved in step 5.

The breadcrumb UI in `CopilotSidebar` (the existing `Role.ToolBreadcrumb`) shows page-control calls with a distinct icon and label ("🧭 navigated to Coruscant") so the user can see *what* SP-4 did, not just the prose narration.

### 6. Server-side: `CopilotAgent` and the `MapAGUI` endpoint

`CopilotAgent.Build()` and `/copilot/stream` need **no behavioural change** beyond accepting the per-turn `tools` field that `MapAGUI` already supports. The agent's `Instructions` get a new section:

```text
PAGE-CONTROL TOOLS:
When the user's turn includes a `tools` catalog containing names prefixed with
the active page slug (e.g. galaxy_map_*), those tools are EXECUTED ON THE PAGE.
They are not data queries — they change what the user is looking at.

Use them when the user's intent is navigational or operational ("take me to",
"show", "highlight", "switch to timeline mode", "play from 22 BBY"). Prefer
them over a prose link when both would work — the point is to drive the page,
not just describe what the user could click.

ALWAYS narrate one short sentence about what you just did: "Centred on
Coruscant in the Core Worlds." Do not echo the tool name or arguments.

If a page-control tool returns Ok=false, USE the error message — propose a
correction ("No exact match for 'Bothan' — Bothawui is the closest. Want me
to go there?") instead of silently re-trying or apologising.

Do NOT mix a page-control call with a `[SUBJECT: …]` resolution call in the
same turn unless necessary. Page actions are cheap; tool budget should still
prefer one navigation + one narrating sentence over a multi-step plan.
```

The protocol-droid persona, the entity-linking rules, the data-source priority — all unchanged. SP-4 still cites KG links in its narration; the page tools are an addition, not a replacement.

### 7. Implementation phases

- **Phase 0 — spike (≤ ½ day).** Confirm the `Microsoft.Agents.AI.AGUI` 1.6.1-preview client exposes a frontend-tool registration hook compatible with what `MapAGUI`'s `RunAgentInput.tools` field forwards. If yes, Phase 1 proceeds. If no, fall back to Alternatives § A.
- **Phase 1 — adopt `AGUIChatClient`.** Frontend takes the package reference, `CopilotSidebar.razor` is rewritten against the typed client. Existing read-only SP-4 behaviour preserved (regression: existing E2E on Yoda still produces prose + citations as today). Hand-rolled `AguiSseReader.cs` deleted in the same change.
- **Phase 2 — galaxy map page control.** `PageControlService` added; `GalaxyMapUnified.razor` registers the 13 actions from § 4; `CopilotAgent.Instructions` gets the page-control block; sidebar breadcrumb adds the page-action icon. Ship behind no flag — the action registry is empty for every other page, so SP-4 elsewhere is byte-identical to today.
- **Phase 3 — opt-in other pages.** Timeline (`timeline_set_year`, `timeline_set_lens`, `timeline_play`), Graph Explorer (`graph_explorer_focus_node`, `graph_explorer_expand`, `graph_explorer_collapse`), Knowledge Graph (`kg_inspect_node`, `kg_switch_tab`), Search (`search_set_query`, `search_set_filter`). One PR per page; the contract is stable.
- **Phase 4 (deferred) — shared state via `STATE_DELTA`.** If a use case appears for SP-4 owning a slice of UI state across turns (e.g. a persistent "watch list" of highlighted systems), introduce a `PageStateService` mirror and bind the relevant UI to it. Not on the critical path for any user request today.

## Alternatives considered

- **A. Keep the hand-rolled SSE reader and extend it with a client-tool dispatcher.** No NuGet adoption risk; no preview package. But every future AGUI capability (state channels, custom events, replay semantics, error codes) becomes our maintenance burden, and the Frontend would drift from the API's `Microsoft.Agents.AI.AGUI` event shape. Acceptable as the Phase 0 fallback if the preview client lacks frontend-tool dispatch; not the preferred path.
- **B. Drive the page from a `STATE_DELTA` channel instead of frontend tools.** Cleaner for "agent owns the selection slot", much worse for "fire-and-confirm a navigation". A navigate-then-confirm verb is what the model already understands as a tool — wrapping it as a JSON-Patch on a synthetic `{ selection: …, mode: …, era: … }` document forces the model to reason about state shape instead of intent, and gives the UI no return-value channel for failure cases ("system not found"). Reserved for the watch-list-style use case in Phase 4.
- **C. A second AGUI endpoint per page (`/copilot/galaxy-map/stream`, `/copilot/timeline/stream`).** Rejected. The whole point of SP-4 (Design-022) is *one* assistant that follows the user across pages with conversation continuity. Per-page endpoints would re-introduce the "I have to retype my question on every page" problem Design-022 solved. The `RunAgentInput.tools` per-turn channel is exactly the right place to express "different tools available on different pages" without splitting the agent.
- **D. Per-page system prompts switched at the server.** Same objection as C — fragments the persona, kills continuity, doubles the prompt-maintenance burden. The current instructions already handle page-aware behaviour via the FACETS envelope; adding a "page-control tools available this turn" rule is one paragraph, not a new agent.
- **E. JavaScript-only routing — sidebar publishes a JS event, page subscribes.** Skips `PageControlService` entirely. Rejected because (a) Blazor circuits make `JSInvokable` round-trips trivial — there's no perf win from going pure JS; (b) the C# service gives us per-action result objects, async cancellation, and a unit-testable surface; (c) all the page methods we're calling already live in `.razor.cs` C#, not JS. Going through JS would mean a `JSInvokable` round-trip back into C# anyway.
- **F. Let SP-4 only emit deep-link URLs and have the Frontend auto-navigate.** Half-measure: covers `galaxy_map_navigate` but nothing else (highlighting, lens, era, fullscreen). Also conflates "the agent suggested I look at this" (a link the user clicks) with "the agent did this for me" (a tool call). Keep both: SP-4 still emits `[Name](/graph-explorer/{pageId})` citation links per its existing entity-linking rules; page-control tools are for *operating* the page, not for surfacing references.

## Open questions

- **`AGUIChatClient`'s frontend-tool API in 1.6.1-preview.** Verified by Phase 0 spike. The blog post and NuGet page both confirm the package exists and the client class is `AGUIChatClient`, but neither documents the frontend-tool handler shape. If the preview is missing it, defer to Alternatives § A and reopen this question when the API stabilises.
- **Migrating `Ask.razor` to `AGUIChatClient`.** Out of scope for Phase 1. Once SP-4 has been on the typed client for ≥ 2 weeks with no regressions, migrate `/ask`. Until then, the shared `Components/Shared/Agui/` DTOs stay — two callers, one wire format.
- **Confirm-before-act for irreversible actions.** Switching from Explore to Timeline mode is mostly harmless; toggling fullscreen is jarring. Default: every action fires immediately, the breadcrumb + narration tells the user what happened, and there's an undo affordance only where the page already has one (`GoBack`). If user feedback says fullscreen-toggle is too startling, gate it behind a confirm-step in the agent prompt rather than a UI dialog.
- **Tool-call budget.** Page actions are cheap (no LLM cost, no MongoDB hit), but each one still consumes a tool-call iteration against `UseFunctionInvocation.MaximumIterationsPerRequest = 8` and the `UseToolCallBudget(softWarnAt: 6, hardLimit: 10)` cap. Likely fine — a typical "take me to X and highlight Y" turn is 2 page calls + maybe 1 KG lookup — but worth measuring after Phase 2 lands.
- **Cross-page actions ("take me to the timeline at 22 BBY").** SP-4 could chain `goto:/timeline?year=-22` via `NavigationManager.NavigateTo` before invoking the new page's action set, but the in-flight tool would need to await page re-registration. Out of scope for Phase 2; revisit when Phase 3 lands and the second page (timeline) is wired.
- **Persistence of agent-driven state across reload.** Today the page restores from the URL deep link (Design-031); SP-4-driven highlights are not in the URL. Acceptable for now — refresh = clean slate is the existing behaviour for filters too. Revisit if users complain.
- **Discoverability.** Should the sidebar empty-state list a "Try: 'take me to Coruscant'" prompt on `/galaxy-map`? Default yes — extend `_galaxyMapGenericPrompts` in `CopilotSidebar.razor` with two navigation-style prompts so users discover the capability.

## Revisit when

- The `Microsoft.Agents.AI.AGUI` package leaves preview (≥ 1.x stable). At that point, lift the Phase 0 spike's verdict and migrate `Ask.razor` to the same client if it hasn't already.
- A use case appears for **persistent agent-owned UI state across turns** (e.g. a "watch list" or "comparison set" SP-4 builds up over a conversation). At that point, introduce `STATE_DELTA`-backed `PageStateService` per Alternatives § B + Phase 4.
- The page-tool catalog grows past ~10 verbs on a single page, or the model starts confusing page actions with data queries despite the `<page>_` prefix discipline. At that point, consider splitting page tools into a separate `tools_page` group surfaced to the model with explicit instructions and a per-group budget.
- A request comes in to give SP-4 a **destructive** verb (delete a chat session, submit a Holocron proposal, edit a node). Re-open this design's non-goals — those belong behind explicit confirmation UI, not behind an LLM tool call. The architecture supports it; the policy doesn't.
- AGUI gains a typed first-class **forwarded-state** channel that subsumes both the page-context envelope (Design-022 wire) and the per-turn `tools` field cleanly. At that point, drop both string-injected channels in favour of the structured one.
