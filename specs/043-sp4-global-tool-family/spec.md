# Design-043: SP-4 Global Tool Family (`sp4_*`)

**Status:** Proposed — 2026-05-23. First member: `sp4_open_wookieepedia_article` ([specs/002-sp4-wookieepedia-modal/](../../specs/002-sp4-wookieepedia-modal/)).

**Date:** 2026-05-23

**Author:** Patrick Magee + Claude

**Related:** [Design-022 Page-Aware Copilot Sidebar](../022-galaxy-map-copilot/spec.md), [Design-041 SP-4 Page Control via AGUI Frontend Tools](../041-sp-4-page-control-agui-frontend-tools/spec.md), [Spec 002 SP-4 Wookieepedia Article Modal](../../specs/002-sp4-wookieepedia-modal/spec.md)

## Problem

Design-041 introduced page-scoped frontend tools registered via `PageControlService.Register("<page>", actions)`. That service is single-page by construction — it throws if a second page tries to register while one already holds the slot — and the agreed naming convention is `<page>_<verb>[_<object>]` so the agent can tell at a glance whether a tool drives the focused page or queries the data layer.

That convention does not have a home for a tool that should be available **on every page where SP-4's sidebar exists**, regardless of focus. The first concrete case is "open the Wookieepedia article for this subject in a modal" (Spec 002). The modal can sensibly be triggered from `/galaxy-map`, `/timeline`, `/character-timelines`, `/ask`, `/knowledge-graph`, or anywhere else `CopilotSidebar` mounts. Forcing every page to register the same `<page>_open_wookieepedia_article` tool would be N copies of the same delegate; forcing the agent to ask the user to switch pages first would be a worse UX than not having the verb at all.

A second concrete case is already foreseeable: a "summarise the current page" or "save this session" verb that crosses page boundaries. Without a documented place to put global tools, the next contributor either:

- Adds their tool to a random page's `PageControlService.Register` call (then their tool quietly disappears on other pages, surprising the user), OR
- Hand-rolls a parallel registration mechanism that does its own thing — fragmenting the agent's tool-discovery story.

## Decision

Introduce a **global SP-4 tool family** with a dedicated registration surface and a dedicated naming prefix.

### 1. Naming convention

- **Page-scoped tools (Design-041)**: `<page>_<verb>[_<object>]`, snake_case. *Unchanged*. Example: `galaxy_map_navigate`, `timeline_set_year`.
- **Global tools (this design)**: `sp4_<verb>[_<object>]`, snake_case. The literal prefix `sp4_` is the agent's own name — easy for both the model and the human reader to recognise. Example: `sp4_open_wookieepedia_article`.

The agent's `InstructionsTemplate` carries a parallel block ("GLOBAL SP-4 TOOLS") alongside the existing "PAGE-CONTROL TOOLS" block so the model learns both families together.

### 2. Registration surface

A new `GlobalCopilotToolsService` lives in `src/StarWarsData.Frontend/Services/`, scoped per Blazor circuit, sibling to `PageControlService`:

```csharp
public sealed class GlobalCopilotToolsService
{
    public IReadOnlyList<AIFunction> Tools { get; }
    public IReadOnlyList<PageAction> Actions { get; }   // reused record from PageControlService

    public GlobalCopilotToolsService(IEnumerable<IGlobalCopilotToolFactory> factories) { /* ... */ }
}

public interface IGlobalCopilotToolFactory
{
    PageAction CreateAction();
}

// DI extension method:
public static IServiceCollection AddGlobalCopilotTool<TFactory>(this IServiceCollection services)
    where TFactory : class, IGlobalCopilotToolFactory;
```

Key differences from `PageControlService`:

- **No `Register` / no mutability.** Global tools are known at app startup; the set is fixed for a circuit's lifetime. Adding a tool requires a code change + restart.
- **Multiple factories supported.** `AddGlobalCopilotTool<T>` registers an `IGlobalCopilotToolFactory`; `GlobalCopilotToolsService` aggregates all DI-registered factories.
- **`PageAction` record is reused** (not duplicated) so the sidebar's "Can drive page" popover renders global and page actions identically — they're just grouped under different headings ("Always available" vs "On this page").

### 3. Merge at submit time

`CopilotSidebar.SubmitAsync` constructs `ChatOptions.Tools` per turn by **union** of global tools and page tools:

```csharp
var globalTools = GlobalCopilotTools.Tools;
var pageTools = PageControl.Tools;
var allTools = globalTools.Count + pageTools.Count > 0
    ? [.. globalTools.Cast<AITool>(), .. pageTools.Cast<AITool>()]
    : null;
var options = new ChatOptions { Tools = allTools };
```

The existing `UseFunctionInvocation` middleware dispatches both families identically — it cares only about `AIFunction.Name` matching, not where the `AIFunction` came from.

### 4. Where types live (deviation note)

Pure-logic helpers used by global tools (URL builders, normalisers, DTOs that have no Frontend dependency) live in `src/StarWarsData.Models/<Domain>/` (e.g. `StarWarsData.Models.Wookieepedia.WookieepediaUrlBuilder`), **not** in `src/StarWarsData.Frontend/Services/`. Reason: the `StarWarsData.Tests` project references `Services` + `Models` but **not** `Frontend`, and the Unit-tier gate (Principle III) requires tests for these helpers. The Frontend-bound classes (the `GlobalCopilotToolsService` itself, the modal service, the dialog component, the tool factory) stay in `Frontend/Services/` and `Components/Shared/`.

This split is a generalisable rule for the `sp4_*` family: **server-or-shared logic in `Models/` or `Services/`, Frontend wiring in `Frontend/`**.

### 5. Article rendering: same-origin proxy, not direct iframe

The first cut of this design pointed the iframe directly at `https://starwars.fandom.com/wiki/<title>?action=render` (body-only MediaWiki output). That failed in two ways validated against a live browser:

1. **Cloudflare bot challenge.** Direct browser embeds of `starwars.fandom.com` URLs hit a Cloudflare CAPTCHA. The user never sees the article; they see a "Verify you are human" check. This affects every `?action=render`, REST v1, and `?useskin=` variant we tried.
2. **No styling.** `?action=render` returns raw `<div class="mw-parser-output">` HTML with no `<head>` and no stylesheet links. Inside the iframe it renders with browser defaults — illegible against the modal's dark frame.

The fix is a **same-origin proxy endpoint on the Frontend**:

```http
GET /wookieepedia/article?title=<title>
```

Implementation in `src/StarWarsData.Frontend/Services/WookieepediaArticleProxy.cs`:

1. Fetches `https://starwars.fandom.com/api.php?action=parse&page=<title>&prop=text|displaytitle&disableeditsection=1&disabletoc=1&format=json&formatversion=2` with a polite `User-Agent` that Cloudflare accepts.
2. Strips Fandom-specific chrome from the returned HTML via a small regex pipeline: `<script>`, `<style>`, comments, edit-section spans, navboxes, ambox/messagebox tables, TOC / category / printfooter divs.
3. Wraps the body in a self-contained `<!DOCTYPE html>` document with an embedded stylesheet tuned for the site's dark theme — orange headings (`#ffa726`), green links (`#66bb6a`), styled `aside.portable-infobox`, blockquote styling, mobile-responsive infobox collapse below 720px.
4. Returns `text/html; charset=utf-8`.

The iframe `src` then points at this same-origin URL. Three wins:

- **No Cloudflare challenge** — Fandom only sees a server-side request with a sane UA, which it accepts.
- **No CORS issues** — the iframe is same-origin to the Frontend so it can be sized/styled normally; the browser doesn't apply cross-origin restrictions.
- **Full control over typography** — the proxy's wrapper CSS makes the article render in the site's visual language even though the body HTML is Fandom's.

The `WookieepediaUrlBuilder.BuildRenderUrl(...)` therefore returns a relative path (`/wookieepedia/article?title=...`) instead of an absolute Fandom URL. `BuildCanonicalUrl(...)` still returns the absolute `https://starwars.fandom.com/wiki/<title>` for the "Open on Wookieepedia" affordance — clicking that opens the full chrome'd page in a new tab through a normal browser navigation, which Cloudflare accepts.

The iframe is sized inline (`style="width:100%;height:80vh;min-height:520px"`) rather than via scoped `.razor.css` because MudDialog renders its content in a portal whose children don't carry the source component's scope attribute — scoped CSS targeting the iframe never applied, and the iframe collapsed to the browser default ~150px. Inline style is the simplest contract and works regardless of where MudDialog mounts the dialog.

### 6. Sandbox posture

The iframe still carries a conservative `sandbox` attribute even though it loads same-origin:

```html
<iframe sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
        referrerpolicy="no-referrer"
        loading="lazy"
        ...>
```

`allow-same-origin` is required for the iframe to load same-origin images/stylesheets. `allow-popups` + `allow-popups-to-escape-sandbox` let in-article links open in a new tab when the user clicks them (the `<base target="_blank">` in the wrapped document directs all clicks to a new tab). **No `allow-scripts`** — the proxy strips `<script>` tags during sanitisation, and we don't want any script that survived to execute.

## Alternatives considered

- **Inline the global tool list in `CopilotSidebar.razor.cs`.** Works for one tool — the next global verb would land as an orphan delegate inside a UI component. Rejected for cohesion.
- **Extend `PageControlService` with a separate `GlobalActions` slot.** Couples two distinct concerns (per-page mutable state, global immutable state) into one service. Rejected for readability.
- **Server-side tool emitted by `CopilotAgent`.** Some global verbs (e.g. opening a modal, modifying URL state) MUST execute in the browser because they touch `IDialogService` / `NavigationManager`. A server-side declaration would just be a stub that signals the client — same wire shape as a client tool, no benefit, extra indirection. Rejected.
- **Per-page registration of the same global tool everywhere.** Every page that mounts `CopilotSidebar` would add a `PageControlService.Register("<page>", [globalToolDuplicate, ...pageTools])` call. N copies, N maintenance burdens, and N+1 pages where the user just lost the tool because someone forgot to register it. Rejected.
- **Use the prefix `global_*` instead of `sp4_*`.** Generic and forgettable. The agent's own name is memorable; the prefix doubles as branding for the user-facing popover ("SP-4 can do these things everywhere"). Stuck with `sp4_*`.

## Consequences

- `CopilotAgent.InstructionsTemplate` gains a "GLOBAL SP-4 TOOLS:" block parallel to the existing "PAGE-CONTROL TOOLS:" block. Future global verbs append to that block; new page verbs go in the page block.
- The sidebar's "Can drive page (N)" chip popover gains an "Always available" group rendered above the existing "On this page" group. The chip label may broaden ("Available tools (N)") as the global count grows.
- New helper types that have no Frontend dependency MUST go in `StarWarsData.Services/<Domain>/` so the Unit-tier gate can cover them without dragging `Frontend` into the test project's reference graph.
- The set of global tools is **closed at startup** — there is no runtime "register a global tool" surface. If a feature genuinely needs runtime-mutable global tools, this design must be revisited.
- A future change that re-organises `CopilotAgent.InstructionsTemplate` (e.g. switching to a templated render) MUST preserve the GLOBAL/PAGE block parallelism; conflating them would force the agent to re-learn the family boundary.

## Revisit when

- The global-tool catalogue grows past ~5 verbs, OR the model starts confusing `sp4_*` with `<page>_*` tools (e.g. calling `sp4_navigate` when `galaxy_map_navigate` was meant) despite the prefix. At that point, consider hosting global tools on a dedicated AGUI stream channel or surfacing them with explicit per-group instructions and budgets.
- A use case appears for **persistent agent-owned UI state across turns** at the global level (a "watch list" the user builds up with SP-4 over a session). At that point, evaluate Design-041 Alternatives § B (`STATE_DELTA`-backed shared document) before adding more imperative `sp4_*` verbs.
- AGUI gains a typed first-class **forwarded-state** channel that subsumes both the page-context envelope (Design-022) and the per-turn `tools` field cleanly. At that point, drop both string-injected channels in favour of the structured one and re-derive what `sp4_*` means in that world.
- A second `IGlobalCopilotToolFactory` implementation lands that needs **lifetime configuration** (singleton vs scoped, lazy vs eager). The current per-circuit scoped + eager aggregation works for one tool; revisit at the second.
