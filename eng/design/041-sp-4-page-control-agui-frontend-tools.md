# Design-041: SP-4 Page Control via AGUI Frontend Tools

**Status:** Partially Implemented — Phases 0, 1, 2a, 2b shipped 2026-05-22; Phase 3 partially shipped 2026-05-22 (Timeline, Character Timelines, Knowledge Graph); `Ask.razor` migration to `AGUIChatClient` shipped 2026-05-22 (the open question from § Open). Phase 0 spike confirmed the preview NuGet's API surface (§ 7). Phase 1 swapped `CopilotSidebar.razor` to `AGUIChatClient` via `IChatClient`. Phase 2a wired the minimal end-to-end path — `PageControlService` + a single `galaxy_map_navigate` action + `UseFunctionInvocation` + the agent prompt block — and the previously-open assumption that `MapAGUI` merges per-turn `RunAgentInput.tools` into the agent catalog is **confirmed** (verified by "take me to Tatooine" centring the map). Phase 2b filled out the full galaxy-map verb set. Phase 3 added the Timeline (eras + categories + select event), Character Timelines (search + open + filter + LLM-as-matcher event focus), and Knowledge Graph (switch tab + node filters + edge filters + clear + list + open node) page-action bundles. Discoverability addressed via a "Can drive page (N)" chip + popover in the sidebar header (resolves the open question of the same name). `Ask.razor` migration drops the hand-rolled `AguiSseReader` + AGUI DTO layer entirely; tool calls / render_* visualisations / multi-turn session replay all flow through `ChatMessage` + `ChatResponseUpdate` now, and the shared `Components/Shared/Agui/` folder is deleted. Smoke-tested 2026-05-22 against `/kernel/stream` end-to-end (Yoda render_markdown + Mace Windu plain text + multi-turn "where was he born?" with prior-turn pronoun resolution). 429 UX preserved via a `RateLimitMessageHandler` that converts a rate-limited response into a typed `RateLimitedException` before `AGUIChatClient.EnsureSuccessStatusCode()` discards the body. Graph Explorer and Search remain to be wired under Phase 3.
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

> **Phase 0 spike (completed 2026-05-21).** Read `Microsoft.Agents.AI.AGUI.xml` from the restored package cache. Public surface is minimal — one type, `AGUIChatClient`, implementing `Microsoft.Extensions.AI.IChatClient`. Single ctor: `(HttpClient, string endpoint, ILoggerFactory, JsonSerializerOptions, IServiceProvider)`. Only methods are `GetResponseAsync` / `GetStreamingResponseAsync` inherited from `IChatClient`. **There is no AGUI-specific frontend-tool registration hook.** The expectation in the original draft of this design (a `RegisterToolHandler("name", handler)` API on the client) was wrong — frontend tools ride the standard M.E.AI tool-invocation pipeline:
>
> 1. Wrap the `AGUIChatClient` with `.AsBuilder().UseFunctionInvocation().Build()` (same pattern the server already uses in [`CopilotAgent.Build`](../../src/StarWarsData.Services/AI/Agents/CopilotAgent.cs#L76-L83)).
> 2. Per turn, populate `ChatOptions.Tools` with `AIFunction.Create(handler, name, description)` instances built from `PageControlService.AvailableActions`. The middleware sees the model's tool call in the streamed response, executes the local delegate, appends the result as a `ChatMessage`, and re-invokes `GetStreamingResponseAsync` automatically.
>
> Server-side prerequisite: the hosting layer's `MapAGUI` handler must merge `RunAgentInput.tools` (declared by the client per turn) into the agent's per-call tool catalog so the model sees them. The XML doc enumerates `IEnumerableAGUITool`, `AGUITool`, and `RunAgentInput` in the source-generated context, which is consistent with the wire shape supporting per-turn tools — but the implementation detail is opaque from the public surface alone. **First Phase 1 task is an end-to-end smoke test confirming a frontend tool registered via `ChatOptions.Tools` actually fires from a `MapAGUI` round-trip.** If the hosting layer ignores per-turn tools, fall back to Alternatives § A. Either way, Phase 2's `PageControlService` contract is unaffected.

### 3. `PageControlService` — the action contract

A scoped singleton on the Frontend, sibling to `PageContextService`. Because frontend tools ride `Microsoft.Extensions.AI` (Phase 0 finding), the executable surface is an `AIFunction` per action — but each action also carries the user-facing strings the sidebar's discoverability popover renders (Open Q resolved: § "Discoverability" below). The service is a thin registry over a `PageAction` record:

```csharp
namespace StarWarsData.Frontend.Services;

// The agent-facing AIFunction plus the user-facing strings the popover renders.
public sealed record PageAction(AIFunction Tool, string Label, string? Example = null);

public sealed class PageControlService
{
    // Actions the focused page advertises. Empty if no page registered.
    public IReadOnlyList<PageAction> Actions { get; }

    // Convenience projection for ChatOptions.Tools — just the AIFunctions.
    public IReadOnlyList<AIFunction> Tools { get; }

    // The slug of the page that owns the current registration ("galaxy_map", "timeline", ...).
    public string? CurrentPage { get; }

    // Page registers in OnInitialized; unregisters in Dispose.
    public IDisposable Register(string page, IReadOnlyList<PageAction> actions);

    public event Action? OnChange;
}
```

Rules:

- Only **one** page can be registered at a time (the focused page). `Register` returns an `IDisposable` whose `Dispose` removes the registration; pages call it from `IDisposable.Dispose` / `IAsyncDisposable.DisposeAsync`. A second `Register` while one is active is a programmer bug and throws — never silently overwrite.
- Each `AIFunction` is built via `AIFunctionFactory.Create(delegate, name, description)` by the page. The page owns the parameter binding (it knows its own types) and the body (it knows its own JS module / component methods). `description` is **model-facing** copy with parameter guidance; `PageAction.Label` is the **user-facing** short title; `PageAction.Example` is one example phrase the user might type. All three render together in the sidebar popover so users learn the vocabulary by seeing it.
- The `UseFunctionInvocation` middleware calls the wrapped delegate on the same SynchronizationContext the chat call was made from — i.e. the Blazor circuit — so handlers can touch component state directly without an explicit `InvokeAsync(...)` wrap. (Confirmed by Phase 2a smoke test; navigation called from the middleware re-renders cleanly.)
- `PageControlService.Tools` is consumed by `CopilotSidebar.SubmitAsync` at submit time as `ChatOptions.Tools`. The server-side tools come from `CopilotAgent.Build()` and don't change; the page tools are per-turn via `RunAgentInput.tools`. **The tool catalog SP-4 sees this turn is the union of the server-side toolkit and whatever the focused page advertises** — switch to `/timeline` and SP-4 loses `galaxy_map_navigate`, gains `timeline_set_year`. No prompt rewrite, no second agent.
- Handler return type is `Task<string>` (or `string`) — a one-line confirmation the agent quotes (`"Centred on Coruscant (system, Core Worlds)."`) or a typed result the agent reasons about. Failures are returned as a string (`"No system named 'Bothan'. Closest match: Bothawui."`) — `UseFunctionInvocation` already serializes any return type to JSON for the model.

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
2. `CopilotSidebar.SubmitAsync` builds a `ChatOptions` with `Tools = PageControlService.AvailableActions` (a list of `AIFunction` from the focused page) and prior conversation `messages` (envelope-prefixed as today). It calls `_chatClient.GetStreamingResponseAsync(messages, options, ct)` where `_chatClient` is the `AGUIChatClient` wrapped with `.UseFunctionInvocation()`.
3. `AGUIChatClient` POSTs to `/copilot/stream` with `RunAgentInput.tools` carrying the page tools' name + JSON-Schema parameters. Server's `MapAGUI` merges those into the per-turn model tool catalog alongside `CopilotAgent`'s static tools.
4. Model emits `TOOL_CALL_START { toolCallName: "galaxy_map_navigate" }` + args. Server has no executor registered for that name (the page tools are declarations only on the server), so it forwards the events to the SSE stream and waits for a tool-result message on the next turn.
5. `AGUIChatClient` surfaces the tool call as an `AIContent` on the streaming response. The wrapping `UseFunctionInvocation` middleware finds a matching `AIFunction` in `ChatOptions.Tools`, invokes it on the Blazor circuit, and appends a `ChatMessage(role: tool, …)` carrying the return value. The middleware then re-invokes `GetStreamingResponseAsync` with the updated message list — transparent to `CopilotSidebar.SubmitAsync`.
6. Server receives the tool-result message in `RunAgentInput.messages`, feeds it back to the model.
7. Model emits `TEXT_MESSAGE_*` ("Centred on Coruscant — the Core Worlds capital. Want me to highlight the surrounding sectors?").
8. Sidebar renders the prose; the map already moved in step 5 (the `AIFunction` body called `_module.InvokeAsync<bool>("drillToDeepLink", …)` before returning).

The breadcrumb UI in `CopilotSidebar` (the existing `Role.ToolBreadcrumb`) shows page-control calls with a distinct icon and label ("🧭 navigated to Coruscant") so the user can see *what* SP-4 did, not just the prose narration. The breadcrumb is populated by hooking into the `UseFunctionInvocation` pipeline (either by subscribing to the streaming updates' `FunctionCallContent` / `FunctionResultContent` items, or by wrapping the page's `AIFunction` instances at registration time so they emit a `UIEvent` before delegating).

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

- **Phase 0 — spike (shipped 2026-05-21).** Read the `Microsoft.Agents.AI.AGUI` 1.6.1-preview public surface from the restored package XML. Findings recorded in the Phase 0 callout above and in the doc's revised § 3 (no custom frontend-tool hook; use M.E.AI `UseFunctionInvocation`). `AguiSseReader.cs` survives — still used by `Ask.razor`.
- **Phase 1 — adopt `AGUIChatClient` (shipped 2026-05-22).** Frontend csproj takes the package reference. `CopilotSidebar.razor` rewritten against `IChatClient`: per-circuit `AGUIChatClient` instantiated in `OnInitialized`, conversation state migrated from `List<AguiMessage>` to `List<ChatMessage>`, streaming-update walker maps `TextContent` / `FunctionCallContent` / `FunctionResultContent` to the existing breadcrumb UI. No regressions on the read-only path — server-executed tools still render as breadcrumbs.
- **Phase 2a — minimal page-control wire (shipped 2026-05-22).** `PageControlService` scoped singleton landed; `GalaxyMapUnified.razor` registers ONE `galaxy_map_navigate(pageId, name?)` action; `CopilotSidebar.razor` wraps the chat client with `.UseFunctionInvocation()` and passes `PageControl.Tools` per turn; `CopilotAgent.InstructionsTemplate` gains the `PAGE-CONTROL TOOLS` block; sidebar header gains a "Can drive page (N)" chip with a popover listing each action's label + description + clickable example phrase. Confirmed end-to-end: "take me to Tatooine" resolves the pageId via `keyword_search`, calls the client tool, the map drills, SP-4 narrates one sentence. Validates the previously-open assumption that `MapAGUI` merges `RunAgentInput.tools` into the per-turn agent catalog.
- **Phase 2b — remaining galaxy-map actions (pending).** The other 12 verbs from § 4 (`galaxy_map_go_overview`, `_go_back`, `_set_mode`, `_toggle_region`, `_show/hide_all_regions`, `_highlight_sector`, `_open/close_details`, `_toggle_fullscreen`, `_set_era`, `_set_lens`, `_play_timeline`). Each is a thin lambda over a method that already exists on `GalaxyMapUnified.razor` — registration follow-ups, not new code paths.
- **Phase 3 — opt-in other pages (partially shipped 2026-05-22).** Timeline (`timeline_set_realm`, `_list_eras`, `_set_eras`, `_list_categories`, `_set_categories`, `_list_events`, `_select_event`) shipped. Character Timelines (`character_timelines_search`, `_open`, `_back_to_search`, `_set_event_types`, `_list_events`, `_focus_event`) shipped — the LLM-as-matcher pattern (read the compact event list, pick the index) is what makes "take me to Maul's timeline where he was exiled" work without embeddings. Knowledge Graph (`knowledge_graph_switch_tab`, `_set_node_filters`, `_set_edge_filters`, `_clear_filters`, `_list_nodes`, `_list_edges`, `_open_node`) shipped — `set_node_filters` and `set_edge_filters` are deliberately one tool each with optional named args so a single call covers "canon starships from the clone wars" without an N-call sequence. Graph Explorer (`graph_explorer_focus_node`, `_expand`, `_collapse`) and Search (`search_set_query`, `_set_filter`) still pending. One PR per page; the contract is stable.
- **Phase 4 (deferred) — shared state via `STATE_DELTA`.** If a use case appears for SP-4 owning a slice of UI state across turns (e.g. a persistent "watch list" of highlighted systems), introduce a `PageStateService` mirror and bind the relevant UI to it. Not on the critical path for any user request today.

## Alternatives considered

- **A. Keep the hand-rolled SSE reader and extend it with a client-tool dispatcher.** No NuGet adoption risk; no preview package. But every future AGUI capability (state channels, custom events, replay semantics, error codes) becomes our maintenance burden, and the Frontend would drift from the API's `Microsoft.Agents.AI.AGUI` event shape. Acceptable as the Phase 0 fallback if the preview client lacks frontend-tool dispatch; not the preferred path.
- **B. Drive the page from a `STATE_DELTA` channel instead of frontend tools.** Cleaner for "agent owns the selection slot", much worse for "fire-and-confirm a navigation". A navigate-then-confirm verb is what the model already understands as a tool — wrapping it as a JSON-Patch on a synthetic `{ selection: …, mode: …, era: … }` document forces the model to reason about state shape instead of intent, and gives the UI no return-value channel for failure cases ("system not found"). Reserved for the watch-list-style use case in Phase 4.
- **C. A second AGUI endpoint per page (`/copilot/galaxy-map/stream`, `/copilot/timeline/stream`).** Rejected. The whole point of SP-4 (Design-022) is *one* assistant that follows the user across pages with conversation continuity. Per-page endpoints would re-introduce the "I have to retype my question on every page" problem Design-022 solved. The `RunAgentInput.tools` per-turn channel is exactly the right place to express "different tools available on different pages" without splitting the agent.
- **D. Per-page system prompts switched at the server.** Same objection as C — fragments the persona, kills continuity, doubles the prompt-maintenance burden. The current instructions already handle page-aware behaviour via the FACETS envelope; adding a "page-control tools available this turn" rule is one paragraph, not a new agent.
- **E. JavaScript-only routing — sidebar publishes a JS event, page subscribes.** Skips `PageControlService` entirely. Rejected because (a) Blazor circuits make `JSInvokable` round-trips trivial — there's no perf win from going pure JS; (b) the C# service gives us per-action result objects, async cancellation, and a unit-testable surface; (c) all the page methods we're calling already live in `.razor.cs` C#, not JS. Going through JS would mean a `JSInvokable` round-trip back into C# anyway.
- **F. Let SP-4 only emit deep-link URLs and have the Frontend auto-navigate.** Half-measure: covers `galaxy_map_navigate` but nothing else (highlighting, lens, era, fullscreen). Also conflates "the agent suggested I look at this" (a link the user clicks) with "the agent did this for me" (a tool call). Keep both: SP-4 still emits `[Name](/graph-explorer/{pageId})` citation links per its existing entity-linking rules; page-control tools are for *operating* the page, not for surfacing references.

## Open questions

- **Confirm-before-act for irreversible actions.** Switching from Explore to Timeline mode is mostly harmless; toggling fullscreen is jarring. Default: every action fires immediately, the breadcrumb + narration tells the user what happened, and there's an undo affordance only where the page already has one (`GoBack`). If user feedback says fullscreen-toggle is too startling, gate it behind a confirm-step in the agent prompt rather than a UI dialog.
- **Tool-call budget.** Page actions are cheap (no LLM cost, no MongoDB hit), but each one still consumes a tool-call iteration against `UseFunctionInvocation.MaximumIterationsPerRequest = 8` and the `UseToolCallBudget(softWarnAt: 6, hardLimit: 10)` cap. Likely fine — a typical "take me to X and highlight Y" turn is 2 page calls + maybe 1 KG lookup — but worth measuring after Phase 2b lands.
- **Cross-page actions ("take me to the timeline at 22 BBY").** SP-4 could chain `goto:/timeline?year=-22` via `NavigationManager.NavigateTo` before invoking the new page's action set, but the in-flight tool would need to await page re-registration. Out of scope for Phase 2; revisit when Phase 3 lands and the second page (timeline) is wired.
- **Persistence of agent-driven state across reload.** Today the page restores from the URL deep link (Design-031); SP-4-driven highlights are not in the URL. Acceptable for now — refresh = clean slate is the existing behaviour for filters too. Revisit if users complain.

## Resolved questions

- **`AGUIChatClient`'s frontend-tool API in 1.6.1-preview.** Resolved 2026-05-21 by Phase 0 spike — no AGUI-specific hook; frontend tools ride standard M.E.AI `ChatOptions.Tools` + `.UseFunctionInvocation()`. Confirmed end-to-end by Phase 2a.
- **Server-side merge of `RunAgentInput.tools`.** Resolved 2026-05-22 by Phase 2a smoke test — `MapAGUI` (1.0.0-preview.260311.1) does merge per-turn client tools into the agent's tool catalog, "take me to Tatooine" works.
- **Discoverability.** Resolved 2026-05-22 — the "Can drive page (N)" chip in the sidebar header opens a MudMenu popover listing each registered `PageAction`'s `Label`, agent-facing `Description`, and clickable `Example` phrase. Reuses the same descriptions the model sees — single source of truth. Empty-state suggestion prompts (the original sketched fallback) are not needed; the chip is visible the moment the page registers.
- **Migrating `Ask.razor` to `AGUIChatClient`.** Resolved 2026-05-22 — pulled forward from the ≥ 2-week soak after the sidebar migration went green on the same day. `Ask.razor` now uses `IChatClient` + `ChatMessage` end-to-end; `AguiSseReader.cs` and the `AguiMessage` DTO are deleted. Renamed `Components/Shared/Agui/` is gone. Three bugs surfaced during validation, all fixed:

  1. **Empty assistant bubbles between tool calls** (cosmetic). `ToChatResponse()` emits one Assistant `ChatMessage` per FunctionCallContent-only segment — fixed by skipping text-empty segments in `ApplyAssistantText`.

  2. **AGUI wire-contract asymmetry surfaces every tool-invocation failure as a turn-killing crash.** This had two parts:

     a. **What the wire actually carries.** Decompile chain proves the asymmetry: M.E.AI 10.5.1 `FunctionInvokingChatClient.CreateFunctionResultContent` (decompile line 4399-4413) substitutes a plain `string` like `"Error: Function failed. Exception: …"` whenever a tool's invocation status isn't `RanToCompletion`. `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 1.0.0-preview.260311.1 `SerializeResultContent` (decompile line 7219-7234) then writes that string **raw** into the SSE `content` field — `Content = result2` when `result is string`. `Microsoft.Agents.AI.AGUI` 1.6.1-preview.260514.1 `DeserializeResultIfAvailable` (decompile line 7197-7204) then runs `JsonSerializer.Deserialize<JsonElement>(content)` and throws `'E' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.` *inside* the streaming iterator — escaping the `await foreach` and killing the turn before the model's text can arrive. The old hand-rolled `AguiSseReader` papered over this by never parsing `content` as JSON; the typed client is strict.

     b. **Why our tools were actually throwing.** Two root causes, both in our code:
        - **Missing `serializerOptions` on every `AIFunctionFactory.Create`.** The [official AG-UI backend tool rendering docs](https://learn.microsoft.com/agent-framework/integrations/ag-ui/backend-tool-rendering) require this for complex parameter types (`List<int>`, `List<Reference>`, our descriptor POCOs). Without it, the framework uses a default `JsonSerializerOptions` that doesn't share a type-info chain with AGUI hosting, so complex args fail to bind. Fixed by threading `IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>` through `AskAIAgent` + `CopilotAgent` + every toolkit's `AsAIFunctions(serializerOptions)` overload.
        - **`Reference` type only accepted the canonical object shape `{ pageId: N, title, url }`.** The model frequently emitted shorthand: `references: [524426]` (bare ints) or `references: ["https://…"]` (bare URLs) — both of which threw `JsonException` at deserialization, M.E.AI substituted `"Error: Function failed. Exception: The JSON value could not be converted to StarWarsData.Models.Queries.Reference."`, hitting the AGUI wire asymmetry. Fixed by a tolerant `ReferenceJsonConverter` in `Models/AI/Ask.cs` that lifts bare ints into `{ pageId }` and bare strings into `{ url }`. Verified by reading the actual model args from the wire diagnostics — `args={"references":[524426], …}` — not speculation.

  3. **`ToolCallBudgetMiddleware` was returning raw strings for short-circuit results** (e.g. `"Duplicate render call blocked: …"`). Wrapped each return in a `BlockedResult` POCO so the AGUI hosting takes its structured-JSON path.

  Two safety nets remain in place: (a) `IncludeDetailedErrors = true` on `UseFunctionInvocation` so M.E.AI's `"Error: …"` fallback strings include the actual exception message — invaluable in the apiservice log when a future tool regression hits; (b) defensive `catch (JsonException)` in `Ask.razor.SubmitAsync` that renders "A tool returned a malformed response" instead of raw `'E' is an invalid start of a value` parser noise, so any future wire-contract miss degrades gracefully.

  Verified end-to-end by Browser-driven smoke tests of all `/ask` mode buttons: Chart (`render_chart`), Graph (`render_graph` — the Skywalker family tree, the original failure mode), Browse (`render_data_table`), Infobox (`render_infobox`). Each rendered cleanly with full Sources cards. The remaining `render_*` tools (`render_table`, `render_timeline`, `render_markdown`, `render_path`, `render_aurebesh`) share the same code path — same `serializerOptions`, same `Reference` converter — and are covered by the same fix.

  Rate-limit UX (precise retry-after countdown) preserved via a `RateLimitMessageHandler` that runs before `AGUIChatClient.EnsureSuccessStatusCode()` discards the body. The Sources/citation card was slimmed alongside the migration: Node (→ `/knowledge-graph/nodes/{pageId}`) + Location (galaxy map) + Wiki only; the Graph Explorer / Timeline / Holocron chips were dropped and that surface area moves to the KG node detail page they all originate from.

## Revisit when

- The `Microsoft.Agents.AI.AGUI` package leaves preview (≥ 1.x stable). At that point, lift the Phase 0 spike's verdict and migrate `Ask.razor` to the same client if it hasn't already.
- A use case appears for **persistent agent-owned UI state across turns** (e.g. a "watch list" or "comparison set" SP-4 builds up over a conversation). At that point, introduce `STATE_DELTA`-backed `PageStateService` per Alternatives § B + Phase 4.
- The page-tool catalog grows past ~10 verbs on a single page, or the model starts confusing page actions with data queries despite the `<page>_` prefix discipline. At that point, consider splitting page tools into a separate `tools_page` group surfaced to the model with explicit instructions and a per-group budget.
- A request comes in to give SP-4 a **destructive** verb (delete a chat session, submit a Holocron proposal, edit a node). Re-open this design's non-goals — those belong behind explicit confirmation UI, not behind an LLM tool call. The architecture supports it; the policy doesn't.
- AGUI gains a typed first-class **forwarded-state** channel that subsumes both the page-context envelope (Design-022 wire) and the per-turn `tools` field cleanly. At that point, drop both string-injected channels in favour of the structured one.
