# Phase 0 Research: SP-4 Wookieepedia Article Modal

**Date**: 2026-05-23

**Plan**: [plan.md](./plan.md)

This document resolves the technical questions that would otherwise be `NEEDS CLARIFICATION` markers in `plan.md`. Each entry follows the **Decision / Rationale / Alternatives** format.

---

## R-001 — Wookieepedia article-body endpoint

**Question**: What URL does the iframe `src` point at to get article body only (no Fandom site chrome)?

**Decision**: `https://starwars.fandom.com/wiki/<URL-encoded title>?action=render`

**Rationale**: MediaWiki's classic `action=render` query parameter returns the parsed article HTML — the same content that occupies the main column of the regular page — without the wrapping `<html>`, top nav, left rail, right rail, footer, comments, or related-page sections. Fandom inherits this MediaWiki contract. It is the smallest, most stable surface that meets FR-003 (modal body MUST contain only the article content). It has been stable on MediaWiki for over a decade and is what every "popup article preview" feature on the open web is built on.

**Alternatives considered**:

- **REST v1 parsoid HTML** (`https://starwars.fandom.com/api/rest_v1/page/html/<title>`) — Wikimedia's newer endpoint, returns clean Parsoid HTML with a section-stable DOM. Heavier (full `<html>` document with extra metadata), requires server-side fetch + reinjection because CORS doesn't allow iframe-loading from a different subdomain pattern, and gives us no advantage over `?action=render` for this use case. Reserved for a future "extract specific sections" feature.
- **Full Fandom page in iframe** — load `https://starwars.fandom.com/wiki/<title>` as-is. Rejected: contradicts the user's stated requirement ("a lot of bloat on wookiepedia website") and FR-003 explicitly forbids it.
- **Scrape + sanitize server-side** — fetch HTML, strip chrome with HTMLAgility, re-render in a `<div>`. Rejected: we own the sanitizer + every future Fandom DOM change is our problem. The iframe sandbox model (R-003) handles trust without our owning HTML cleaning.
- **Stored `Page.Content` markdown** — already in MongoDB, no live fetch. Rejected: markdown loses images/tables/infobox formatting, and goes stale between ETL syncs. The user explicitly chose live HTML over stored markdown.

---

## R-002 — Where the global SP-4 tool registers

**Question**: `PageControlService.Register(...)` is single-page (throws if a second page tries to register). Where does a tool that should be available on every page register itself?

**Decision**: Introduce **`GlobalCopilotToolsService`** as a per-circuit scoped service, sibling to `PageControlService` at `src/StarWarsData.Frontend/Services/GlobalCopilotToolsService.cs`. It exposes a static `IReadOnlyList<AIFunction> Tools` populated from DI-injected tool factories at construction time — no `Register` API, no per-page mutability. `CopilotSidebar.SubmitAsync` (line 451) merges the two lists into `ChatOptions.Tools` at submit time:

```csharp
var pageTools = PageControl.Tools;
var globalTools = GlobalCopilotTools.Tools;
var allTools = pageTools.Count + globalTools.Count > 0
    ? [.. globalTools.Cast<AITool>(), .. pageTools.Cast<AITool>()]
    : null;
var options = new ChatOptions { Tools = allTools };
```

The existing `UseFunctionInvocation` middleware ([CopilotSidebar.razor:355](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor#L355)) dispatches global and page tools identically — it cares only about the `AIFunction.Name` matching the model's tool call, not where the `AIFunction` came from.

**Rationale**:

- Mirrors the existing `PageControlService` contract (per-circuit scoped, `Tools` property as the agent-facing surface) so future readers learn one pattern, not two.
- `GlobalCopilotToolsService` has no `Register` because global tools are known at app startup. Per-feature DI extension methods (`builder.Services.AddGlobalCopilotTool<WookieepediaArticleToolFactory>()`) keep the wiring discoverable.
- Cleanly composable with [the "Can drive page" popover at CopilotSidebar.razor:78-98](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor#L78-L98) — the popover gains a "Always available" group above the "On this page" group.

**Alternatives considered**:

- **Inline a `_globalTools` list in `CopilotSidebar.razor.cs`** — works for one tool, but the next global SP-4 verb (likely a "save my session" / "summarize this page" / etc.) lands as an orphan delegate inside a UI component. Rejected for cohesion reasons.
- **Extend `PageControlService` with a separate `GlobalActions` slot** — couples two distinct concerns into one service and reads worse than two small services. Rejected.
- **Server-side registration** — make the tool an AGUI server-tool emitted by `CopilotAgent`. Rejected: the *execution* MUST live in the browser (it has to call `IDialogService.ShowAsync`), so a server-side tool would just be a stub that signals the client. We already have a per-turn client-tool mechanism (PageControlService + `RunAgentInput.tools`) that is the correct primitive.

---

## R-003 — iframe sandbox posture

**Question**: What `sandbox` and related attributes does the iframe use?

**Decision**:

```html
<iframe
    src="..."
    sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
    referrerpolicy="no-referrer"
    loading="lazy"
    title="Wookieepedia article: {{articleTitle}}"
    style="width:100%; height:100%; border:0;">
</iframe>
```

**Rationale**:

- `allow-same-origin` — Fandom CSS/images load with relative URLs that need same-origin resolution against `starwars.fandom.com`. Without this, the article body renders as raw unstyled text.
- `allow-popups` + `allow-popups-to-escape-sandbox` — internal article links (`<a href="/wiki/...">`) opened by the user should navigate to a new browser tab on Fandom rather than reload the iframe. Without `escape-sandbox` they'd inherit our restrictive sandbox and break.
- **No `allow-scripts`** — Fandom's `?action=render` output should not require JS to display. The first implementation iteration MUST validate this in browser; if a critical article (e.g. one with interactive infobox accordion behaviour) fails to render readably without scripts, escalate by re-evaluating R-001 (perhaps switch to REST v1) before adding `allow-scripts` — adding scripts widens the trust boundary materially.
- `referrerpolicy="no-referrer"` — don't leak our origin to Fandom's analytics / cookie consent flow. Cleaner privacy posture.
- `loading="lazy"` — modal mounts the iframe immediately; lazy loading is a safety net against rapid open/close that would otherwise spam Fandom with requests.
- `title` — accessibility (`AT-LMG` chunk readers announce the iframe).

**Alternatives considered**:

- **No sandbox at all** — exposes us to anything Fandom's HTML might do (e.g. cookie-setting via `<img>`-tracking pixels). Rejected; the cost of the sandbox is zero behaviour change for the user.
- **`allow-scripts`** — convenience but materially expands trust surface. Deferred to a follow-up if real articles break without it; validation Phase MUST cover this.

---

## R-004 — pageId → title resolution

**Question**: When the agent passes a `pageId` (preferred per FR's parameter-order guidance), how does the Frontend resolve it to the canonical Wookieepedia page title?

**Decision**: Reuse the existing sidebar citation-resolution path. CopilotSidebar already resolves `/graph-explorer/{pageId}` references inline in [CopilotSidebar.razor.cs around line 478-482](../../src/StarWarsData.Frontend/Components/Shared/CopilotSidebar.razor#L478-L482) by calling the same API endpoint that powers the entity-detail page. The modal service injects the same `ApiClient` (or whatever interface CopilotSidebar already uses for that resolution) and calls `GetByPageIdAsync(pageId)` which returns `{ Title, Continuity, WikiUrl }` from `kg.nodes` — satisfying Principle VI (no `raw.*` reads).

When the agent passes a `title` directly (fallback), the modal service skips the lookup and uses the title as-is — the title goes straight into the URL builder.

**Rationale**: Single-source-of-truth for entity lookup. The KG already exposes pageId → title via the API; introducing a parallel resolver would create the second-source-of-truth bug that Principle VI explicitly exists to prevent.

**Alternatives considered**:

- **Direct MongoDB call from Frontend** — Frontend doesn't have a MongoDB driver (and shouldn't — Principle II's defence-in-depth). Rejected.
- **Always pass title from the agent, never pageId** — pushes lookup cost onto the agent's context and risks the agent fabricating a non-existent title from a half-remembered name. The KG resolution is deterministic; let the agent prefer pageId when it has one from a prior search.

---

## R-005 — Continuity suffix resolution

**Question**: When and how is the `/Legends` suffix applied to the title?

**Decision**: Applied at URL-build time by `WookieepediaUrlBuilder`, based on `GlobalFilterService.Continuity` snapshot at the moment `OpenAsync` is called. Mapping:

| `Continuity` value | URL title          | "Switch to Legends" affordance shown? |
|--------------------|--------------------|---------------------------------------|
| Canon              | `<Title>`          | No                                    |
| Legends            | `<Title>/Legends`  | No                                    |
| Both               | `<Title>` (canon)  | Yes — clicking re-opens with `/Legends` |

The suffix is appended **before** URL encoding because `/` is a path separator that MUST NOT be percent-encoded for this MediaWiki convention. The title portion before the slash is URL-encoded normally (spaces → `%20` or `_`; non-ASCII → percent-escaped); the literal `/Legends` is appended as-is.

The "Switch to Legends" affordance does NOT trigger a new SP-4 turn — it calls back into the same `WookieepediaArticleModalService.OpenAsync(currentTitle, forceLegends: true)`, replacing the iframe `src` in the existing dialog. No tool call, no narration.

**Rationale**:

- Matches Wookieepedia's own URL convention: `Coruscant` (canon), `Coruscant/Legends` (legends).
- Snapshot-at-open is the simplest semantics: the user gets the article matching the filter they had set *when they asked*. The spec edge-case for "filter changes while modal open" explicitly chooses non-reactive behaviour.
- The in-modal switch affordance keeps users in flow when they're on the `Both` filter and realise mid-read they want the Legends variant — without forcing them back to SP-4.

**Alternatives considered**:

- **Reactive subscription** — modal auto-reloads iframe when filter changes. Rejected per spec edge-case: surprising during reading.
- **Always show "Switch" affordance** — would be visual noise on Canon-only / Legends-only articles. The variant switch is meaningful only when both exist, and we don't pre-check existence (would require a HEAD request to Fandom). Solution: show affordance only on `Both` and let the linked URL 404-gracefully if no Legends variant exists. Footer text differs subtly: "View canonical Wookieepedia page" + "View Legends variant".

---

## R-006 — Tool naming convention departure

**Question**: Design-041 § "Naming convention" says `<page>_<verb>[_<object>]` snake_case is mandatory; this feature uses `sp4_<verb>` instead because it isn't page-bound. Is this a constitutional issue?

**Decision**: No — it is a deliberate, documented deviation that graduates into a new convention. The new design doc `specs/043-sp4-global-tool-family/spec.md` (a tasked deliverable) records:

- The `<page>_*` rule still applies for page-scoped tools registered via `PageControlService`.
- A new prefix family `sp4_*` is introduced for **global** SP-4 tools registered via `GlobalCopilotToolsService`. The prefix is `sp4_` exactly so the agent can tell at a glance whether a call is page-scoped (`galaxy_map_navigate`) or sidebar-scoped (`sp4_open_wookieepedia_article`).
- Tool name for this feature: **`sp4_open_wookieepedia_article`** (verb-first, object-second).

`CopilotAgent.InstructionsTemplate` gains a "GLOBAL SP-4 TOOLS" block alongside the existing "PAGE-CONTROL TOOLS" block, explaining when each family applies.

**Rationale**: Principle V's boundary is clear — a *standing* rule that future global SP-4 verbs (likely 2-3 more in the next year) will follow MUST live in a design doc, not in a single feature's plan. The convention is exactly the kind of "graduated decision" the constitution describes.

**Alternatives considered**:

- **Skip the prefix, just name it `open_wookieepedia_article`** — works for one tool, but the next global verb (e.g. `summarise_current_page`) would have no recognisable namespace. Rejected for forward-consistency.
- **Use `global_` as the prefix** — generic and forgettable. `sp4_` is the agent's own name (it's the brand the user sees), making the family memorable for both agent and reader.

---

## R-007 — Modal-stack behaviour vs. existing `MudDialog` instances

**Question**: FR-009 says only one article modal at any time. What if another `MudDialog` is already open (e.g. `HolocronProgressDialog`)? Are these stacked or mutually exclusive?

**Decision**: The "one at a time" constraint applies **only to article modals**, not to all MudDialog instances on the page. `WookieepediaArticleModalService` tracks its own `IDialogReference?` and only closes that one before opening a new article. Other modals (Holocron progress, future confirm dialogs, etc.) continue to stack on top of the article modal per MudBlazor's default behaviour. If both an article modal and a Holocron dialog are open simultaneously, the Holocron dialog renders on top — same as MudBlazor's normal layering. User-driven dismissal of the article modal does not affect any other open dialog.

**Rationale**: The spec's "single instance" wording is about **avoiding redundant article modals stacking** ("user asks SP-4 for a second article — replace, don't stack"). It is not a global "no two MudDialogs may coexist" rule, which would conflict with existing UX (Holocron dialog can legitimately open while user is reading an article).

**Alternatives considered**:

- **Close ALL MudDialogs before opening an article** — destructive, would dismiss an in-progress Holocron run. Rejected.
- **Block opening a new article if any other dialog is open** — surprising to the user; the article modal would silently fail to open. Rejected.

---

## R-008 — Test surface for the LLM-driven path

**Question**: How is the SP-4-driven path (user types → agent calls tool → modal opens) tested without an Agent-tier test?

**Decision**: The LLM-driven path is **not unit-tested in the Tests project** — it is **manually validated** via Chrome DevTools MCP per Principle IV. The validation script (in `quickstart.md`) runs against a real AppHost and exercises the five P1 / P2 / P3 acceptance scenarios. Unit tests cover the deterministic surfaces (URL building, continuity suffixing, title sanitation, single-instance modal semantics tested via a fake `IDialogService`).

**Rationale**: Per Principle III, Agent-tier tests are nightly/manual only. Adding one for this feature would slow down the CI gate without value — the LLM behaviour (recognising "show me the article for X" as a tool call) is exactly the kind of behaviour Agent tier exists for and the rest of the LLM features rely on the same manual-validation discipline (Design-041 Phase 2a, Phase 3).

**Alternatives considered**:

- **Add an Agent-tier test** — pays the OpenAI cost every nightly run forever. Rejected; manual validation in `quickstart.md` is the consistent project standard.
- **Stub the LLM and unit-test the tool delegate directly** — already covered (URL builder tests + modal service tests with a fake `IDialogService`). The agent-routing is the gap and that gap is intentional.
