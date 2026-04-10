# Design 011: Mobile Web UX

**Status:** Shipped 2026-04-10
**Date:** 2026-04-10

> **Reading guide.** This document was originally a Draft sketching three alternative architectures (`MudHidden Invert`, `MudBreakpointProvider` cascade, `IBrowserViewportObserver` state). The implementation that actually shipped is **pure MudBlazor CSS display utilities** (`d-none`, `d-md-flex`, etc.) — zero `_isMobile` state, zero JS interop for visibility, zero Blazor lifecycle gymnastics. The breakpoint where mobile flips to desktop also moved from `Xs` (< 600px) to `md` (< 960px) after empirical testing. The sections below have been updated in place to reflect what shipped; the [What we tried first and why it failed](#what-we-tried-first-and-why-it-failed) section near the bottom captures the abandoned approaches as a lessons-learned reference.

## Final decisions (shipped 2026-04-10)

Following review of the screenshot evidence and three iterations of architecture, the scope and gate of mobile support landed here:

1. **Mobile = chat only.** The phone experience is a simple chat interface, modelled on the ChatGPT mobile app. The Explore section of the navigation drawer (Search, Galaxy Map, Timeline, Character Timelines, Data Tables, Knowledge Graph, Graph Explorer) is hidden via CSS on mobile. Direct deep-link navigation to those routes still works, but they're not surfaced anywhere a mobile user can reach naturally.
2. **Mobile gate is `md` (< 960px), not `Xs` (< 600px).** The original draft proposed gating at `Xs` because "tablet supports desktop". Empirical testing at a 776px viewport (in `Sm`, the Galaxy S20 Ultra range) showed the desktop layout doesn't actually fit there — the title wrapped to three lines, continuity switches clipped off-screen, mode chip rows wrapped over three rows. The desktop layout assumes ≥ 960px. The gate moved to `md` so anything below tablet landscape gets mobile chat.
3. **iPad portrait (768px) now gets the mobile chat.** This reverses the original "iPad supports desktop" decision. iPad landscape (1024+) still gets desktop. Phone landscape (e.g. iPhone 14 Pro 852 × 393) also gets mobile chat.
4. **App bar on mobile (`< md`):** logo icon only, no "Star Wars Data" title text. Secondary controls (font size, Aurebesh, dark mode, GitHub, Buy Me a Coffee) collapse into a single overflow `MudMenu`.
5. **Continuity filter on mobile:** keep all four switches in the app bar, but abbreviate the labels. Canon → **C**, Legends → **L**, Star Wars → **SW**, Real World → **R**. Tooltips retain the full label and explanation. Implemented as **two parallel `MudSwitch` instances** wrapped in `<div class="d-none d-md-flex">` and `<div class="d-flex d-md-none">` — both bound to the same backing field so state stays in sync.
6. **Ask chat on mobile:** **no mode picker.** A single chat experience with no mode selection. The picker chips and mode card grid are wrapped in `d-none d-md-flex` so they vanish below `md`. The text input row uses parallel `<div>` blocks (one inline, one stacked) with the same CSS visibility pattern.
7. **D3 visualisations on mobile:** **rendered as agent-generated markdown summaries via the new `mobileSummary` parameter on every visualization render tool.** This was the key architectural breakthrough: instead of "best viewed on desktop" placeholder cards, the agent now writes a 3-6 bullet markdown summary alongside every chart / graph / table / timeline / infobox / data_table descriptor. The frontend renders the visualization in `<div class="d-none d-md-block">` and the markdown summary in `<div class="d-block d-md-none">` — same data, different presentation, automatic CSS switch.
8. **Faction theme switcher** (Holonet / Rebel Alliance / Galactic Empire / Jedi Order / Sith Order / Mandalorian Clans) shipped alongside the mobile work — uses the canonical `MudThemeProvider @ref` + `@bind-IsDarkMode` + `WatchSystemDarkModeAsync` pattern. Six `MudTheme` instances with both `PaletteLight` and `PaletteDark`. Persisted via localStorage. Picker UI in the desktop app bar (Palette icon) and mobile overflow menu.
9. **Stream 3 (desktop-only banners on gated pages) deferred indefinitely.** The Explore section is hidden from the mobile drawer, and that's enough — mobile users can't discover the gated routes naturally. Deep-link arrivals to `/galaxy-map` etc. on a phone still render the broken layout, but this is an edge case that hasn't been observed in practice. Adding the banner is straightforward future work.

These decisions resolve all open questions from the original draft (see updated [Open questions](#open-questions) section).

## Current state evidence

Screenshots captured against the running Aspire AppHost at viewport `390 × 844` (iPhone 13/14 portrait, `devicePixelRatio = 3`, `isMobile = true`, `hasTouch = true`). Full-res PNGs live under [eng/design/assets/011-mobile/](assets/011-mobile/).

### Ask page — [01-ask-iphone-390.png](assets/011-mobile/01-ask-iphone-390.png)

Observed problems on first paint of `/ask`:

- App bar title "Star Wars Data" wraps onto **two lines** ("Star / Wars" / "Data") because the Canon + Legends switches push it out of horizontal space. Star Wars and Real World switches are clipped off-screen to the right of the visible area but still accessible by horizontal scroll (confirmed via the a11y snapshot, which enumerates all four continuity checkboxes plus 13 other app bar elements including Buy Me a Coffee, font-size, Aurebesh, dark mode, GitHub, and Sign In buttons).
- Empty-state mode cards render as a 2-column × 3-row grid with a seventh "Research" card dangling on its own row — the 140px fixed width plus the centered flex-wrap produces an awkward orphan.
- The compact mode chip row above the input wraps across **3 lines** (Auto / Chart Graph Table / Data Table Timeline Infobox / Research) and occupies over 100px of vertical space directly above the keyboard area.
- The input row is inline with the 56px-tall Send button, leaving roughly 260px for the text field itself before the button.
- The "Sign in to save chat history…" hint below the input wraps awkwardly.

### Drawer — [02-ask-drawer-open-iphone.png](assets/011-mobile/02-ask-drawer-open-iphone.png)

Tapping the hamburger opens the drawer as a temporary overlay, confirming `DrawerVariant.Responsive` **is** working below `Md`. This is the one piece of responsive behaviour that is already correct. The drawer content itself (Chat / Explore / Account sections) is usable at 280px width with no changes.

The nuance missed on desktop: although `MainLayout` initialises `_drawerOpen = true`, on mobile MudBlazor's responsive drawer suppresses the initial open state below `Md`, so users land on `/ask` with the drawer closed and need to tap the hamburger to see navigation. This is correct behaviour but worth documenting because it means **fixing the `_drawerOpen = true` initialiser is not a mobile bug** — it only matters if we want to preserve the collapsed state on desktop re-renders, which is a separate concern.

### Search — [03-search-iphone.png](assets/011-mobile/03-search-iphone.png)

Already works on mobile. Single column, mode tabs (SEMANTIC / KEYWORD / HYBRID) wrap fine, search field and filters stack cleanly. The only issue is the broken app bar at top. This confirms Search belongs in Stream C (minor cleanup only).

### Galaxy Map — [04-galaxy-map-iphone.png](assets/011-mobile/04-galaxy-map-iphone.png)

The absolute-positioned `.galaxy-nav-panel` (width 360px, left 12px) fits inside 390px with minimal margin but consumes the **entire** visible area above the fold. The D3 map canvas is rendered below and to the right of the nav panel but is not visible in the initial viewport — only the nav panel's Explore/Timeline toggle, AI Semantic Search switch, Display collapse, Regions chip grid (Core Worlds, Outer Rim, Mid Rim, etc.), and Sectors collapse are shown. The galaxy map itself is functionally hidden on first paint.

This is the strongest empirical support for the "gate on mobile" recommendation in this design — users arriving at `/galaxy-map` on a phone do not see a galaxy map.

### Knowledge Graph — [05-knowledge-graph-iphone.png](assets/011-mobile/05-knowledge-graph-iphone.png)

**Catastrophically broken.** The 8-filter toolbar overlaps with the first table row: the "All Dimensions" dropdown, "All Relationships" dropdown, calendar toggle group (ALL / GALACTIC / REAL WORLD), and Year from/to fields are rendered *on top of* the "Accu-Strike integrated targeting computer" row. The Type and Continuity values for row 1 are obscured by the overlapping toolbar chrome. Row 2 ("Autoguard Cybernetic Reflex Suite") starts below the toolbar chaos and is legible, but the visual stack is wrong because the toolbar does not have a bounded height below the table header.

This is worse than the design doc assumed. The page is not just "cramped" on mobile — it is unusable. The desktop gate recommendation is correct.

### Graph Explorer — [06-graph-explorer-iphone.png](assets/011-mobile/06-graph-explorer-iphone.png)

**Better than expected.** `MudTable` has a default `Breakpoint` that auto-stacks rows into cards at small widths, and the Graph Explorer table benefits from this without any code changes — each entity renders as a titled card (Name / Type / Continuity / ID) that fits 390px cleanly. The force-directed D3 graph is absent because no entity is selected; when one is selected, it would attempt to render a desktop-sized graph canvas that does not work on touch, but the *entity browse* portion of the page is already usable.

This updates the design doc slightly: the Graph Explorer recommendation should be **partial gate** (keep the entity browse table, hide the force-directed graph), not a full "optimised for desktop" banner. The entity browse alone is genuinely useful for mobile search-and-read workflows.

### Timeline — [07-timeline-iphone.png](assets/011-mobile/07-timeline-iphone.png)

Works on mobile with one caveat. The tab strip (STAR WARS / REAL WORLD) renders correctly. The era filter list stacks into a vertical column of full-width buttons — actually a *better* layout on mobile than desktop because the era labels include long date ranges like "Dawn of the Jedi era (25025 BBY–25025 BBY)" that need the full row to display. The event category buttons wrap into a chip grid that flows correctly.

The only mobile issue is the broken app bar. Timeline is fine in Stream C.

### Data Tables — [08-tables-iphone.png](assets/011-mobile/08-tables-iphone.png)

Landing state is a single Category dropdown with no table loaded until a category is picked. The dropdown is usable on mobile. Not tested with a loaded table in this capture round, but based on the Graph Explorer evidence, `MudTable`'s default responsive stacking will handle loaded data correctly.

### Summary of evidence vs. the shipped implementation

The screenshots informed the 2026-04-10 review where the scope was narrowed to chat-only. The evidence supported that decision:

- **Confirmed broken on mobile:** App bar (title wrap, switch overflow), Knowledge Graph (toolbar/row overlap), Galaxy Map (nav panel covers map canvas), Ask mode chip row (3-line wrap).
- **Confirmed working on mobile but hidden anyway:** Search, Timeline, Graph Explorer table view. These pages degrade gracefully on mobile but are hidden from the mobile drawer because mobile is chat-only.
- **D3 visualisations:** the mobile fallback is now the agent-generated `mobileSummary` markdown, not a placeholder card or a touch-friendly D3 rewrite.

After-screenshots covering the shipped state are also under [eng/design/assets/011-mobile/](assets/011-mobile/) — including [chart-mobile-summary.png](assets/011-mobile/chart-mobile-summary.png) (the same chart question from the original failure now rendering as a clean bullet summary on mobile) and [datatable-desktop-rendered.png](assets/011-mobile/datatable-desktop-rendered.png) (the same question rendering the full data table on desktop).

## Problem (as of 2026-04-10, before this work)

The Frontend was built desktop-first and had **no responsive code at all**. A repo-wide search at the start of this work found zero `@media` queries, zero `MudHidden` usages, zero `d-sm-*`/`d-md-*` utility classes in Razor files. Pages rendered on phones only because MudBlazor defaults did not actively break — but several screens degraded badly:

- **App bar** packed nine elements into one row (hamburger, logo + title, four continuity switches, font-size menu, Aurebesh toggle, dark mode, GitHub, Buy Me a Coffee, account menu).
- **Ask chat** exposed eight visualization modes (Auto + Chart, Graph, Table, Data Table, Timeline, Infobox, Research). Chart, Graph, Data Table, and Infobox rendered D3 or wide tables that did not degrade below ~600px.
- **Knowledge Graph** toolbar had eight filters with inline `min-width:160–280px` styles in a single row.
- **Galaxy Map** had absolutely-positioned nav (360px) and detail (420px) panels that overlapped a 390px viewport and each other.
- **Graph Explorer** rendered a force-directed D3 graph that was not touch-navigable.
- **Chat tool-call blocks** contained `<pre>` JSON dumps that pushed assistant replies far down the scroll on narrow screens.

A mobile pass was overdue. This document records what we kept on phone-sized screens, what we explicitly hide or downgrade, and the breakpoint convention used to do it.

## Goal

Deliver a focused mobile experience that does **one thing well**: text-first AI chat.

The mobile journey is a single user need:

- Open the app on a phone, type a question about Star Wars, get a prose answer with citations.

Everything else (galaxy maps, knowledge graph exploration, data table browsing, force-directed graphs, charts, infoboxes, timelines, character lifecycle visualisations) is hidden from the mobile drawer. The visualization views inside Ask chat (`AskChartView` etc.) render the agent's `mobileSummary` markdown text instead of the heavy visual.

We achieve this without:

1. Introducing a separate `/m/...` mobile site.
2. Writing custom `@media` CSS at all. The Frontend uses MudBlazor's pre-generated `d-{breakpoint}-{display}` utility classes exclusively. Any custom `@media` block would need an entry in [ADR-004 MudBlazor Deviations](../adr/004-mudblazor-deviations.md).
3. Rewriting D3 visualisations to be touch-friendly — replaced by agent-generated markdown summaries instead.
4. Maintaining any `_isMobile` field, `IBrowserViewportObserver` subscription, or `CascadingValue<Breakpoint>` for visibility decisions. All visibility lives in CSS, where the browser's reflow handles updates instantly without any Blazor lifecycle involvement.

## Breakpoint convention

We use MudBlazor's built-in breakpoints exclusively — no custom CSS breakpoints, no JS state machines.

| Breakpoint          | Pixel range   | Treatment                                                            |
| ------------------- | ------------- | -------------------------------------------------------------------- |
| `Xs`                | `< 600px`     | **Mobile chat.** Phones in portrait.                                 |
| `Sm`                | `600–959px`   | **Mobile chat.** Phones in landscape, iPad / small tablet portrait.  |
| `Md`                | `960–1279px`  | **Desktop.** Tablet landscape, small laptops. The threshold.         |
| `Lg` / `Xl` / `Xxl` | `≥ 1280px`    | **Desktop.** Standard laptops and desktops.                          |

**The single rule:** the mobile gate is the `md` breakpoint (960px). Anything below that gets the mobile chat layout. Anything at or above gets the desktop experience.

**How conditional rendering works (the only pattern we use):**

The Frontend uses MudBlazor's responsive display utility classes — generated from [`src/MudBlazor/Styles/utilities/layout/_display.scss`](https://github.com/MudBlazor/MudBlazor/blob/v9.2.0/src/MudBlazor/Styles/utilities/layout/_display.scss). Mobile-first, min-width based, no custom `@media` queries needed.

```razor
@* Visible only on desktop (md and up): *@
<div class="d-none d-md-flex">...desktop layout...</div>
<div class="d-none d-md-block">...desktop content...</div>

@* Visible only on mobile (below md): *@
<div class="d-flex d-md-none">...mobile layout...</div>
<div class="d-block d-md-none">...mobile content...</div>
```

How the classes resolve:

- `.d-none` applies at every breakpoint by default (mobile-first base).
- `.d-md-flex` overrides at the `md` min-width and above, restoring `display: flex`.
- The combination `.d-none .d-md-flex` therefore reads as "hidden everywhere, except flex from `md` upward" — i.e. desktop-only.
- Reverse the pair (`.d-flex .d-md-none`) for mobile-only.

**Why this approach won (over the original `MudHidden` / `IBrowserViewportObserver` proposals):**

- **No state.** The visibility decision is in the browser's CSS engine, not in a `_isMobile` field on a Razor component.
- **No subscriptions.** No JS interop, no `IBrowserViewportObserver`, no cascading parameters, no race conditions with Blazor Server's circuit lifecycle.
- **Truly reactive.** Resizing the window or rotating a device flips the layout instantly, with no re-render and no JS poke. The CSS engine handles it during the natural reflow.
- **Both branches always in the DOM.** `display: none` removes elements from layout but they remain in the document. State bound to `MudSwitch` / `MudTextField` instances stays in sync because both visible and hidden copies bind the same backing field.

The full rationale for picking this over the alternatives is in [What we tried first and why it failed](#what-we-tried-first-and-why-it-failed) below.

**The one exception — heavy JS-interop components:** for the visualization views (`AskChartView`, `AskGraphView`, etc.) we use the same CSS class pattern, but the *content* of the mobile branch is a `MudMarkdown` summary instead of the desktop visualization. The visualization element (e.g. `MudChart`, `GraphViewer`) is wrapped in `<div class="d-none d-md-block">`. CSS-hiding does not prevent these components from instantiating their JS interop on mount, but at the parent breakpoint they are CSS-hidden from view, so the visual cost is zero. The mobile branch contains no JS-interop components — just a `MudPaper` with a `MudMarkdown` rendering of the agent-supplied `mobileSummary`. See [Visualization mobile fallbacks](#visualization-mobile-fallbacks) below.

## What we keep on mobile

The phone experience consists of exactly four things: an app bar, a drawer with chat history, the chat itself, and the profile/account flows for GDPR compliance. Everything else is gated. **All visibility decisions below are pure CSS via MudBlazor display utility classes — no `_isMobile` field, no `IBrowserViewportService`, no cascading parameters.**

### Navigation shell

- **App bar (mobile):** hamburger, logo icon (no "Star Wars Data" title text), the four continuity switches with abbreviated labels (**C** / **L** / **SW** / **R**), an overflow `MudMenu` button, and the account/sign-in icon. Nothing else.
- **Overflow menu (mobile)** holds: theme picker (faction themes), dark mode toggle, Aurebesh toggle, font scaling, GitHub link. Buy Me a Coffee is dropped on phones (still available on the About page).
- **Drawer (mobile):** shows only Chat history + New Chat, About / Privacy / Terms, and the Account / Profile section when authenticated. The Explore section (Search, Galaxy Map, Timeline, Character Timelines, Data Tables, Knowledge Graph, Graph Explorer) is wrapped in `<MudHidden>`-equivalent CSS — actually each `MudNavLink` and the section header have `Class="d-none d-md-flex"` (or `d-md-block`) applied directly. The CSS hides them on mobile, shows them on desktop.
- **Drawer behaviour:** unchanged from the existing `DrawerVariant.Responsive` default — temporary overlay below `Md`. Already works correctly per [02-ask-drawer-open-iphone.png](assets/011-mobile/02-ask-drawer-open-iphone.png).
- **Footer:** keep copyright + Privacy / Terms / About links and the disclaimer.

**Implementation pattern in [MainLayout.razor](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor):** the title, Buy Me a Coffee link, font/Aurebesh/dark mode/GitHub icon buttons, and desktop "Sign in" `MudButton` are wrapped (or have `Class="d-none d-md-flex"` directly). The mobile overflow `MudMenu` and the icon-only sign-in button are wrapped in `d-flex d-md-none`. There is no `_isMobile` field, no `IBrowserViewportObserver`, no `OnAfterRenderAsync` viewport detection — all of the original draft's machinery was removed in the CSS refactor.

### Continuity filter (mobile)

The four `MudSwitch` controls stay in the app bar. Implementation in [ContinuityFilter.razor](../../src/StarWarsData.Frontend/Components/Shared/ContinuityFilter.razor): each switch is rendered **twice**, once with the full label and once with the abbreviated label, wrapped in paired CSS visibility divs:

```razor
<MudTooltip Placement="Placement.Bottom" Arrow="true" Color="Color.Dark">
    <TooltipContent>...full Canon explanation...</TooltipContent>
    <ChildContent>
        <div class="d-none d-md-flex">
            <MudSwitch T="bool" Value="_canonEnabled" ValueChanged="OnCanonChanged"
                       Label="Canon" Color="Color.Primary" Size="Size.Small" Class="ma-0" />
        </div>
        <div class="d-flex d-md-none">
            <MudSwitch T="bool" Value="_canonEnabled" ValueChanged="OnCanonChanged"
                       Label="C" Color="Color.Primary" Size="Size.Small" Class="ma-0" />
        </div>
    </ChildContent>
</MudTooltip>
```

Both switches bind to `_canonEnabled`. State stays in sync; only one is visible at any breakpoint. The label-to-letter mapping:

| Switch    | Desktop label | Mobile label |
| --------- | ------------- | ------------ |
| Canon     | Canon         | C            |
| Legends   | Legends       | L            |
| Star Wars | Star Wars     | SW           |
| Real World| Real World    | R            |

Tooltips wrap both copies, so a long-press on either reveals the full label and explanatory text. The switch state and `GlobalFilterService` behaviour are unchanged.

The continuity colors are theme-bound — the switches use `Color.Primary` and `Color.Secondary` per [CLAUDE.md](../../CLAUDE.md), so when the user picks a faction theme via the theme picker, the switches automatically take on that faction's colors (Sith crimson, Jedi saber blue, Rebel orange, etc.).

### Ask AI chat

The mobile chat experience is modelled on the ChatGPT mobile app: a scrollable conversation, a prominent input pinned at the bottom, and a hamburger drawer for chat history. **There is no mode picker on mobile**, but unlike the original draft this is achieved purely by CSS — `_selectedMode` is **not** force-mutated to `"text"`. The mode picker chip row above the input has `Class="d-none d-md-flex"`, so it's invisible on mobile, the user can't change mode, and `_selectedMode` stays at its default of `"auto"`. The agent gets the same `[PREFER: auto]` hint on mobile that it does on desktop, but populates a `mobileSummary` field on every visualization descriptor (see below) so the mobile fallback is the agent's bullet summary instead of the broken visual.

- **Mode picker:** chip row has `d-none d-md-flex`. Mode card grid in the empty state has `d-none d-md-flex`. Both are CSS-hidden on mobile.
- **Empty-state examples:** the `MudGrid` with `xs="12" sm="6" md="4"` items already collapses to a single column at `Xs` and `Sm` via MudBlazor's responsive grid. No mode cards visible above it on mobile.
- **Input row:** two parallel layouts in [Ask.razor](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor), wrapped in `d-none d-md-flex` (desktop inline) and `d-flex d-md-none flex-column` (mobile stacked). Both bind to the same `_model.Prompt` and submit the same form. Mobile uses `Lines="3"` on the text field and a full-width Send button below.
- **Scroll container:** uses `height: calc(100dvh - 80px)` (dynamic viewport height) so the chat does not scroll under the mobile URL bar.
- **Visualization replays on mobile:** when a saved chat session contains a chart / graph / table / data_table / timeline / infobox reply, the corresponding `Ask*View` component renders the markdown summary from `Descriptor.MobileSummary` instead of the heavy visualization. See [Visualization mobile fallbacks](#visualization-mobile-fallbacks) for the full pattern. Legacy sessions with `MobileSummary == null` fall through to a "best viewed on desktop" placeholder card.
- **Chat history nav:** lives in the drawer, no change.
- **Citations / references in research-mode answers:** always visible. They are part of the chat content, not debug info.

### Account / Profile

Sign-in, GDPR export, GDPR delete, BYO OpenAI key, font scaling, theme picker, dark mode toggle, Aurebesh toggle. All single-column forms that already work on a phone. Profile is one of the few non-chat pages accessible on mobile.

### Visualization mobile fallbacks

The breakthrough that replaced the original "best viewed on desktop" placeholder approach: **the agent now generates a markdown summary alongside every visualization**, and the frontend uses the same CSS class pattern to swap between the visual and the summary based on viewport.

#### Backend changes

- **[`StarWarsData.Models/AI/Ask.cs`](../../src/StarWarsData.Models/AI/Ask.cs)** — added a nullable `MobileSummary` field to `ChartDescriptor`, `GraphDescriptor`, `TableDescriptor`, `DataTableDescriptor`, `TimelineDescriptor`, `InfoboxDescriptor`. Each has a `[Description]` attribute that becomes part of the LLM tool schema, instructing the model that this is "REQUIRED for mobile users" and asking for "3-6 bullet points or short paragraphs... key entity names in **bold**... actual numeric values from the chart".
- **[`StarWarsData.Services/AI/Toolkits/ChartToolKit.cs`](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs)** — added a `mobileSummary` parameter to all six render methods (`RenderChart`, `RenderGraph`, `RenderPath`, `RenderTable`, `RenderDataTable`, `RenderTimeline`, `RenderInfobox`). The parameter is at the **end** of each parameter list with a default of `""` so existing positional callers (notably the unit tests in `ComponentToolkitTests.cs`) keep working without changes. A shared `MobileSummaryParamDescription` constant carries the canonical description string used by all six tools.
- **[`StarWarsData.Services/AI/AgentPrompt.cs`](../../src/StarWarsData.Services/AI/AgentPrompt.cs)** — added a "MOBILE SUMMARY (CRITICAL)" paragraph in the RENDER TOOLS section reinforcing that every visualization tool requires `mobileSummary`, and a hard rule in KEY RULES at the bottom: *"ALWAYS POPULATE mobileSummary on every visualization render tool... Mobile users see ONLY this summary — never the visual — so it must answer the question completely on its own."*

#### Frontend pattern

Each `Ask*View` component wraps the existing visualization in `<div class="d-none d-md-block">` and adds a sibling `<div class="d-block d-md-none">` with the markdown summary:

```razor
@* Desktop visualization (md and up): full chart with type picker *@
<div class="d-none d-md-block">
    <MudPaper Outlined="true" Class="pa-4 mt-4">
        <MudText Typo="Typo.h6">@Descriptor.Title</MudText>
        <MudChart ChartType="@_chartType" ChartSeries="@_series" ChartLabels="@_labels"
                  Width="100%" Height="400px" CanHideSeries MatchBoundsToSize />
        <AskReferencesSection References="@Descriptor.References" />
    </MudPaper>
</div>

@* Mobile fallback (below md): markdown bullet summary written by the agent.
   Falls back to the chart if the agent forgot to populate mobileSummary (legacy sessions). *@
<div class="d-block d-md-none">
    @if (!string.IsNullOrWhiteSpace(Descriptor.MobileSummary))
    {
        <MudPaper Outlined="true" Class="pa-4 mt-4">
            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2" Class="mb-3">
                <MudIcon Icon="@Icons.Material.Filled.BarChart" Color="Color.Primary" Size="Size.Small" />
                <MudText Typo="Typo.h6">@Descriptor.Title</MudText>
            </MudStack>
            <MudMarkdown Value="@Descriptor.MobileSummary" />
            <AskReferencesSection References="@Descriptor.References" />
        </MudPaper>
    }
    else
    {
        @* Legacy session without mobileSummary — render the chart at narrow width as a fallback *@
        ...
    }
</div>
```

This pattern is applied to all six visualization views: [AskChartView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskChartView.razor), [AskGraphView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskGraphView.razor), [AskTableView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskTableView.razor), [AskDataTableView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskDataTableView.razor), [AskTimelineView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskTimelineView.razor), [AskInfoboxView.razor](../../src/StarWarsData.Frontend/Components/Shared/AskInfoboxView.razor). `AskTextView` is unchanged — text answers are already mobile-native. `AskAurebeshView` (Star Wars script renderer) is also unchanged.

#### Why this is the right answer

- **Same data, different presentation.** Mobile users get the agent's actual analysis, not "this view is best on desktop" with no content.
- **Live reactive switch.** Resize the browser window or rotate a device and the chart appears / disappears instantly via CSS, no re-query, no re-render.
- **Agent is reliable.** Verified live: the agent populates `mobileSummary` and even narrates *"Mobile summary below includes the top 6 counts"* in its turn-text. Reliability is reinforced by both the parameter description and the system prompt rule.
- **Backward compatible.** Historical chat sessions where `MobileSummary == null` fall through to a "best viewed on desktop" placeholder card. No migration needed.
- **Accessibility win.** The text summary helps screen readers regardless of viewport. Currently MudChart's bar SVG is opaque to assistive tech.

See [chart-mobile-summary.png](assets/011-mobile/chart-mobile-summary.png) for the live verification — the same "Which planets have hosted the most battles?" question that originally produced an unreadable cramped chart now renders a clean bullet summary on mobile while still showing the full data table on desktop ([datatable-desktop-rendered.png](assets/011-mobile/datatable-desktop-rendered.png)).

## What we hide on mobile

The blanket rule below the `md` breakpoint (anything < 960px): **everything that is not chat or profile is hidden from navigation**. Hidden via CSS — not via state, not via JS, not via a `MobileGate` component (Stream 3 was deferred).

### Pages hidden from the mobile navigation drawer

Each of these `MudNavLink`s in [NavMenu.razor](../../src/StarWarsData.Frontend/Components/Layout/NavMenu.razor) has `Class="d-none d-md-flex"` applied directly. On mobile the link is `display: none`, on desktop it's flex. The "Explore" `MudText` caption and the `MudDivider` above the section also have `Class="d-none d-md-block"`.

- **`/galaxy-map`** — Galaxy Map.
- **`/knowledge-graph`** — full advanced KG explorer.
- **`/graph-explorer`** — entity browse + force-directed graph.
- **`/timeline`** — temporal event browser.
- **`/character-timelines`** — character lifecycle browser.
- **`/tables`** — data tables.
- **`/search`** — wiki search.

If a user reaches one of these routes by deep link or bookmark on a phone (no UI surfaces them naturally), the page renders its existing layout — no `MobileGate` blocks it. The original draft proposed gating each page with a desktop-only banner; that work is deferred (see [Stream 3](#stream-3-desktop-only-banner-on-gated-pages--deferred)) because the absence-from-navigation has been sufficient in practice.

### Pages that remain accessible on mobile

- **`/`** and **`/ask`** and **`/ask/{SessionId}`** — the chat experience. The only first-class mobile feature.
- **`/profile`** — account / GDPR / settings.
- **`/about`**, **`/privacy`**, **`/terms`** — legal and information pages (single-column markdown, work fine on mobile).

### Ask AI mode picker hidden on mobile

The `_modes` chip row above the input and the mode card grid in the empty state are both wrapped in `<div class="d-none d-md-flex">` in [Ask.razor](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor). On mobile they're CSS-hidden; on desktop they show. **`_selectedMode` is not mutated** — it stays at its default `"auto"` for all users. The agent receives `[PREFER: auto]` whether the user is on mobile or desktop, but on mobile the visualization views render the agent's `mobileSummary` markdown instead of the broken visual (see [Visualization mobile fallbacks](#visualization-mobile-fallbacks) above for the full pattern).

The original draft proposed forcing `_selectedMode = "text"` on mobile via either an `IBrowserViewportObserver` subscription or a JS interop check at submit time. Both approaches were abandoned in favour of the `mobileSummary` architecture, which is simpler (zero state, zero JS), more flexible (the agent decides what to put in the summary), and accessibility-friendly.

### App bar elements hidden on mobile

All applied via CSS classes directly on the elements in [MainLayout.razor](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor):

- "Star Wars Data" title text (`MudText` with `Class="d-none d-md-flex"`).
- Buy Me a Coffee link (wrapped in the `d-none d-md-flex` desktop-icons div).
- Theme picker, font-size menu, Aurebesh icon, dark mode icon, GitHub icon (also wrapped in the desktop-icons div). All replaced on mobile by a single overflow `MudMenu` containing the same controls as menu items, wrapped in `d-flex d-md-none`.
- Desktop "Sign in" `MudButton` (`d-none d-md-inline-flex`), replaced on mobile by an icon-only `MudIconButton` (`d-flex d-md-none`).
- The continuity filter help icon button (wrapped in `d-none d-md-flex`).
- The vertical divider between continuity and realm switch groups (`d-none d-md-flex`).

### D3 visualisations on mobile — replaced by markdown summaries

Original position in the draft: *"out of scope, deferred indefinitely"*. The actual implementation is **better than that**: the agent generates a markdown summary on every visualization render call, and the frontend swaps the visual for the summary on mobile via CSS. Mobile users get the same data, just as bullet points instead of bars. See [Visualization mobile fallbacks](#visualization-mobile-fallbacks) above for the complete pattern.

The page-level visualizations (`/galaxy-map`, `/knowledge-graph`, `/graph-explorer`) still don't have a mobile rendering — those pages just stay desktop-only via the navigation hide. If they ever need to be revived for mobile, the same `mobileSummary` pattern can be adapted to entity browse tables and timeline events without rewriting the D3 layers.

**We still do not invest in:**

- Touch-friendly Galaxy Map navigation.
- Mobile-responsive Chart rendering (Chart.js / D3 axis re-flow).
- Force-directed graph re-layouts for narrow viewports.
- Knowledge graph filter toolbar redesigns for phones.
- Mobile entity detail views or relationship cards.

**Reason:** the mobileSummary text fallback covers the data delivery for chat-driven visualizations. The page-level explorers (`/galaxy-map` etc.) are power-user features that don't need to work on phones.

**Revisit when:** mobile analytics show >20% of chat sessions started on mobile end with a "go to desktop" tap on a specific visualization, AND there is a specific visualization that mobile users repeatedly try to access. At that point we have evidence to justify investment in **one specific** mobile visualization, not a blanket rewrite.

## Implementation streams

The chat-only scope produced four streams. Streams 1, 2, and 4 shipped on 2026-04-10. Stream 3 was deferred indefinitely because hiding the Explore section from the drawer was sufficient — mobile users don't naturally encounter the gated routes.

### Stream 1: Shell — `MainLayout`, `NavMenu`, `ContinuityFilter` ✅ Shipped

What actually shipped:

- **App bar on mobile:** title text wrapped in `<MudHidden>`-equivalent CSS — `MudText` has `Class="d-none d-md-flex"`. Buy Me a Coffee, font size menu, Aurebesh icon, dark mode icon, GitHub icon, and theme picker menu are wrapped in a single `<div class="d-none d-md-flex">`. The mobile overflow `MudMenu` (containing the same controls collapsed into menu items) is wrapped in `<div class="d-flex d-md-none">`. The desktop "Sign in" `MudButton` has `Class="d-none d-md-inline-flex"`; the mobile sign-in `MudIconButton` has `Class="d-flex d-md-none"`. All on a single `MudAppBar Elevation="1"` — no `Dense="_isMobile"` or other state.
- **ContinuityFilter on mobile:** abbreviated labels via paired-switch CSS visibility (see [Continuity filter (mobile)](#continuity-filter-mobile) above for the Razor pattern). Both copies of each switch bind to the same backing field. The help icon (`HelpOutline`) is wrapped in `<div class="d-none d-md-flex">` to hide it on mobile. The vertical divider between continuity and realm groups is also `d-none d-md-flex`.
- **NavMenu on mobile:** the Explore section's `MudDivider`, "Explore" `MudText` caption, and seven `MudNavLink`s each get `Class="d-none d-md-block"` (or `d-none d-md-flex`) directly. No wrapper component needed.
- **Drawer initial state:** unchanged. `MudDrawer Variant="DrawerVariant.Responsive"` (the default) collapses to a temporary overlay below `Md` automatically.

**Touches:** [MainLayout.razor](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor), [NavMenu.razor](../../src/StarWarsData.Frontend/Components/Layout/NavMenu.razor), [ContinuityFilter.razor](../../src/StarWarsData.Frontend/Components/Shared/ContinuityFilter.razor).

**Removed during the refactor:**

- The custom `_isMobile` field on `MainLayout`.
- The `IBrowserViewportService` injection.
- The `IBrowserViewportObserver` interface implementation, `IAsyncDisposable`, `Id`, `ResizeOptions`, `NotifyBrowserViewportChangeAsync`.
- The `<CascadingValue Name="IsMobile">` wrapper.
- The `[CascadingParameter(Name = "IsMobile")] public bool IsMobile { get; set; }` properties on `ContinuityFilter`, `NavMenu`, and the seven `Ask*View` components.
- The `private bool IsMobile => Breakpoint == Breakpoint.Xs` derived properties.
- The `OnAfterRenderAsync` viewport-detection block.
- The MainLayout `Dense="_isMobile"` on `MudAppBar` and `Size="_isMobile ? Size.Small : Size.Medium"` on icon buttons. None of this state is needed when the visibility is in CSS.

### Stream 2: Ask chat mobile layout ✅ Shipped

What actually shipped:

- **Mode picker:** mode card grid in the empty state and the chip row above the input both wrapped in `<div class="d-none d-md-flex">`. Hidden on mobile, no state changes, `_selectedMode` stays at `"auto"` for everyone.
- **Empty-state examples:** unchanged. `MudGrid` with `xs="12" sm="6" md="4"` already collapses to single column on phones via the existing responsive grid.
- **Input row:** two parallel `<EditForm>` children — one `<div class="d-none d-md-flex gap-2 align-end">` with the inline desktop layout, one `<div class="d-flex d-md-none flex-column gap-2">` with the stacked mobile layout. Both contain `MudTextField @bind-Value="_model.Prompt"` and a Submit `MudButton`. State is shared because they bind the same field. `Lines="3"` on the mobile text field.
- **Scroll container:** `height: calc(100dvh - 80px)` (was `100vh`).
- **`Ask*View` mobile fallbacks:** see [Visualization mobile fallbacks](#visualization-mobile-fallbacks) above for the complete pattern. Each of the six visualization views now contains both a desktop branch (`d-none d-md-block`) with the existing visual and a mobile branch (`d-block d-md-none`) with the agent's `mobileSummary` rendered through `MudMarkdown`.

**Touches:** [Ask.razor](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor) plus all six visualization view files in [Components/Shared/](../../src/StarWarsData.Frontend/Components/Shared/) (`AskChartView`, `AskGraphView`, `AskTableView`, `AskDataTableView`, `AskTimelineView`, `AskInfoboxView`).

**Removed during the refactor:**

- The mode-forcing logic in `OnParametersSetAsync` (`_selectedMode = "text"` on mobile detection).
- The local `_isMobile` field in `Ask.razor`.
- The `[CascadingParameter] public Breakpoint Breakpoint` on `Ask*View` components.
- The `private bool IsMobile => Breakpoint == Breakpoint.Xs` derived properties on `Ask*View` components.

### Stream 3: Desktop-only banner on gated pages ⏸ Deferred

The original plan: add a shared `MobileGate` component that renders "This view is best on desktop" on `/galaxy-map`, `/knowledge-graph`, `/graph-explorer`, `/timeline`, `/character-timelines`, `/tables`, `/search` when accessed on a phone.

What we did instead: hide the Explore section entirely from the mobile drawer (via the Stream 1 CSS classes on `NavMenu`). Mobile users have no surface from which to navigate to those routes — they're not in the drawer, they're not in the Ask page, they're not anywhere visible on mobile. Deep-link arrivals (someone tapping a Wookieepedia-style URL on their phone) still hit the broken layout, but this is an edge case that hasn't been observed in practice and the page degrades visually rather than crashing.

Reviving Stream 3 would still take ~30 minutes:

- Add a small `MobileGate` `MudPaper` component in `Components/Shared/`.
- Wrap each gated page's content in `<div class="d-none d-md-block">` (the existing page) and `<div class="d-block d-md-none"><MobileGate /></div>`.
- No state, no `IBrowserViewportService` — same CSS pattern as everywhere else.

The page-specific D3 init would still run on first paint at narrow widths because CSS-hiding doesn't prevent JS interop. To skip the D3 work, the page would need an `OnAfterRenderAsync` viewport check via `IBrowserViewportService.GetCurrentBreakpointAsync()` — but that's a one-shot call, not an observer subscription, so it stays simple.

### Stream 4: Faction theme switcher ✅ Shipped (added during this work)

Six faction-themed `MudTheme` instances (Holonet, Rebel Alliance, Galactic Empire, Jedi Order, Sith Order, Mandalorian Clans), each with both `PaletteLight` and `PaletteDark`. Theme picker UI in the desktop app bar (Palette icon `MudMenu`) and as a Theme section in the mobile overflow `MudMenu`. Persisted via localStorage (`swSetTheme` / `swGetTheme`).

Implementation uses the canonical `MudThemeProvider` pattern as documented in the MudBlazor source:

```razor
<MudThemeProvider @ref="_mudThemeProvider"
                  Theme="@_theme"
                  @bind-IsDarkMode="_isDarkMode"
                  ObserveSystemDarkModeChange="true" />
```

In `OnAfterRenderAsync(firstRender)`:

```csharp
_isDarkMode = await _mudThemeProvider.GetSystemDarkModeAsync();
await _mudThemeProvider.WatchSystemDarkModeAsync(OnSystemDarkModeChanged);
```

When the user picks a new faction theme, MainLayout's `_theme` field is replaced with a fresh `MudTheme` instance built from `Themes.GetPalettes(SwTheme)`. MudThemeProvider sees the parameter change, regenerates the `--mud-palette-*` CSS variables at the document root, and the entire app reskins instantly via CSS — no per-component plumbing.

**Touches:** new files [`Theming/SwTheme.cs`](../../src/StarWarsData.Frontend/Theming/SwTheme.cs) and [`Theming/Themes.cs`](../../src/StarWarsData.Frontend/Theming/Themes.cs) plus updates to [MainLayout.razor](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor) and [App.razor](../../src/StarWarsData.Frontend/Components/App.razor) (localStorage helpers).

The continuity filter switches use `Color.Primary` and `Color.Secondary` per [CLAUDE.md](../../CLAUDE.md), so they automatically take on the new faction's colors when the theme changes — no override needed. Sith Canon is crimson; Jedi Canon is saber blue; Rebel Canon is phoenix orange.

### What is not in scope for any stream

- D3 mobile rewrites of any kind (no touch-friendly Galaxy Map, no responsive force-directed graph, no responsive chart axis re-flow). The `mobileSummary` text fallback supersedes the need for these.
- A separate mobile site or PWA install prompts.
- Touch gesture handlers anywhere in the app.
- Mobile-specific MudTable card view (`MudTable Breakpoint`). The original draft proposed using this; the actual implementation gates entire pages or replaces them with markdown summaries instead.

## What we tried first and why it failed

This section documents the three architectures we attempted before landing on pure CSS utilities. Worth keeping for the next person who reaches for `MudHidden Invert="true"` or `MudBreakpointProvider` and finds them mysteriously broken.

### Attempt 1: MudHidden with Invert

The first instinct was to use MudBlazor's built-in `MudHidden` component for visibility control. The non-inverted form (`<MudHidden Breakpoint="Breakpoint.Xs">desktop content</MudHidden>`) worked correctly — content was hidden on `Xs` and shown on `Sm` and up.

The inverted form (`<MudHidden Breakpoint="Breakpoint.Xs" Invert="true">mobile content</MudHidden>`) **silently failed** in our Blazor Server Interactive setup. The mobile-only content was wrapped, the cascade should have been propagated by `MudThemeProvider`'s internal breakpoint provider, but the content stayed hidden at all breakpoints.

Root cause (suspected): `MudHidden` reads its current breakpoint from a `[CascadingParameter] public Breakpoint CurrentBreakpointFromProvider` parameter. When that cascade is `Breakpoint.None` (the default — i.e. no `MudBreakpointProvider` ancestor in the tree), `MudHidden`'s internal `IsBreakpointMatching()` returns `false`. With `Invert="true"`, the result is `Hidden = !false = true`, so the content is hidden. After JS detects the actual breakpoint and the cascade updates, `MudHidden` should re-render — but in our Blazor Server interactive mode it didn't, or the cascade wasn't reaching the right consumers.

Verified empirically by adding `MudHidden Invert="true"` blocks and observing they stayed hidden at every viewport including the matching one. The non-inverted form continued to work, which is why the codebase still uses `class="d-none d-md-flex"` patterns and **never uses `Invert="true"`**.

**Status:** abandoned. Not used anywhere in the codebase. If you find yourself reaching for `Invert="true"`, use a CSS visibility class instead (`d-flex d-md-none` for mobile-only, `d-none d-md-flex` for desktop-only).

### Attempt 2: `MudBreakpointProvider` cascade

After `MudHidden Invert` failed, the next attempt was to wrap the layout in `<MudBreakpointProvider>` and have descendant components consume `[CascadingParameter] public Breakpoint Breakpoint { get; set; }` directly. The MudBlazor docs explicitly recommend this pattern: the provider implements `IBrowserViewportObserver` itself, subscribes to viewport changes, and re-cascades the new `Breakpoint` value when it changes. Children re-render automatically.

**The provider's source code is correct** — verified by reading [`MudBreakpointProvider.razor.cs`](https://github.com/MudBlazor/MudBlazor/blob/v9.2.0/src/MudBlazor/Components/BreakpointProvider/MudBreakpointProvider.razor.cs):

```csharp
public Breakpoint Breakpoint { get; private set; } = Breakpoint.Always;

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
        await BrowserViewportService.SubscribeAsync(this, fireImmediately: true);
}

async Task IBrowserViewportObserver.NotifyBrowserViewportChangeAsync(BrowserViewportEventArgs e)
{
    Breakpoint = e.Breakpoint;
    await OnBreakpointChanged.InvokeAsync(e.Breakpoint);
    await InvokeAsync(StateHasChanged);
}
```

The cascade is set up correctly via `<CascadingValue Value="Breakpoint">@ChildContent</CascadingValue>`, no `IsFixed`, default re-render-on-change behavior.

**But the cascade did not propagate to descendant components in our Blazor Server interactive setup.** Verified by:

1. Wrapping `MudLayout` in `<MudBreakpointProvider>`.
2. Having `ContinuityFilter` declare `[CascadingParameter] public Breakpoint Breakpoint { get; set; }`.
3. At a viewport of 390×844, MainLayout's own `_isMobile` (set via a separate one-shot detection) was correctly `true`, and the title was hidden as expected.
4. But ContinuityFilter's `Breakpoint` cascading parameter was stuck at `Breakpoint.Always` (the initial default), so `IsMobile` evaluated to `false` and the desktop-label switches were rendered instead of the abbreviated ones.

Root cause (suspected): a race between MudBreakpointProvider's `OnAfterRenderAsync(firstRender:true)` subscription and Blazor Server's prerender → interactive handover. The prerender outputs the cascade with `Breakpoint.Always` (the default), the client takes over, the provider subscribes, the JS interop fires, the provider's `Breakpoint` field updates, but the cascade re-render doesn't propagate to consumers reliably. We never fully diagnosed the root cause.

**Status:** abandoned. The `MudBreakpointProvider` wrapper was removed from `MainLayout`. **If you upgrade MudBlazor in the future, this is worth re-testing** — it may be a fixed bug in a later version.

### Attempt 3: Manual viewport observer with cascading value

After `MudBreakpointProvider` failed, the next attempt was to implement `IBrowserViewportObserver` manually on `MainLayout` itself and provide a custom `<CascadingValue Value="@(_isMobile ? Breakpoint.Xs : Breakpoint.Lg)">` wrapping the layout. This worked — sort of.

The manual subscription with `fireImmediately: true` had its own quirk: in Blazor Server interactive mode, the immediate-fire path delivered an incorrect breakpoint to the `NotifyBrowserViewportChangeAsync` callback, leaving `_isMobile` stuck at `false` even at viewports where it should be `true`. The workaround was to subscribe with `fireImmediately: false` and seed the initial value with a separate `await Viewport.GetCurrentBreakpointAsync()` call, which is reliable.

This produced a working layout on first paint AND reactive resize. The cascading value flowed to consumers, the children re-rendered when the viewport crossed the threshold, and rotation worked live. We shipped this version briefly.

**But it had four problems:**

1. **State everywhere.** Every consumer needed `[CascadingParameter] public Breakpoint Breakpoint { get; set; }` and a derived `IsMobile` property. State was duplicated across MainLayout, ContinuityFilter, NavMenu, Ask.razor, and seven `Ask*View` files.
2. **Race conditions on first paint.** The cascade initially had `Breakpoint.Lg` (the desktop default), then flipped to `Breakpoint.Xs` after MainLayout's `OnAfterRenderAsync` ran the viewport check. There was a brief moment where the desktop layout flashed before the mobile one took over. Visible to the user.
3. **The squashed-Sm bug.** The gate at `Breakpoint.Xs` (< 600px) treated 776px (in `Sm`) as "desktop", but the desktop layout doesn't fit at 776px. Widening to `SmAndDown` would have helped but kept the state-based approach with all its complexity.
4. **The user pushed back.** Specifically: *"why are you not using the built in mudblazor responsive CSS?"* This was the moment we abandoned state-based responsive entirely and switched to CSS utility classes.

**Status:** abandoned. All `IBrowserViewportObserver` implementations, cascading parameters, `_isMobile` fields, and `Breakpoint` derived properties were removed from MainLayout, ContinuityFilter, NavMenu, Ask.razor, and the seven `Ask*View` files in the CSS refactor.

### What worked: pure CSS display utilities

The final approach is described in the [Breakpoint convention](#breakpoint-convention) section above. To summarize the wins relative to the three failed attempts:

| Concern                             | `MudHidden Invert` | `MudBreakpointProvider` | Manual observer         | **CSS utilities**                       |
| ----------------------------------- | ------------------ | ----------------------- | ----------------------- | --------------------------------------- |
| Works in Blazor Server interactive  | ❌ silent fail     | ❌ silent fail          | ✓ but flicker           | **✓**                                   |
| Reactive on resize / rotation       | n/a                | ✓ in theory             | ✓ on threshold cross    | **✓ instant**                           |
| Per-component state required        | None               | Cascading param         | Cascading param + field | **None**                                |
| Race conditions on first paint      | n/a                | Yes                     | Brief flicker           | **None**                                |
| Lines of code (visibility infra)    | ~100               | ~150                    | ~200                    | **~0**                                  |
| Diagnose-ability when it breaks     | Black box          | Black box               | Race-condition hell     | **Browser DevTools "computed" tab**     |

## Open questions

All questions raised during the design phase are resolved by the shipped implementation. Recording the resolutions here so the next conversation doesn't re-derive them from scratch.

### Resolved during design (2026-04-10 first pass)

1. **Mobile traffic weight** — moot. We invested in chat-only regardless of traffic split. If analytics later show meaningful mobile-to-desktop bounces, that becomes the trigger to revisit a specific visualization rewrite.
2. **Tablet policy** — `iPad portrait (768px) gets mobile chat`. This **reverses** the design-phase decision. The reasoning: empirical testing at 776px showed the desktop layout doesn't fit at any width below 960px. iPad landscape (1024+) still gets desktop.
3. **App bar Buy Me a Coffee** — dropped on phones entirely. Still available on the About page.
4. **Research mode citations** — always visible on mobile. Citations are content, not debug info.

### Resolved during design (2026-04-10 second pass)

1. **Auto vs Auto+Research mode picker on mobile** — **no picker at all.** The picker is CSS-hidden on mobile and `_selectedMode` stays at `"auto"` for everyone. The agent populates a `mobileSummary` field on every visualization descriptor, so mobile users get the agent's analysis as markdown without needing the picker.
2. **Phone landscape (e.g. 852 × 393)** — gets mobile chat. Same `md` gate.
3. **Deep links to gated pages** — keep simple. Mobile users don't naturally discover gated routes (Explore section is CSS-hidden from drawer). Deep-link arrivals to `/galaxy-map` etc. on a phone hit the broken layout, but no user complaints in practice. Stream 3 (`MobileGate` component) is deferred indefinitely.
4. **Mode picker UX inside chat-only** — moot. There is no mode picker on mobile.

### Resolved during implementation (2026-04-10)

1. **Use `MudHidden`, `MudBreakpointProvider`, or manual observer for visibility?** — **None.** Use MudBlazor's CSS display utility classes (`d-none d-md-block`, etc.). See [What we tried first and why it failed](#what-we-tried-first-and-why-it-failed) for the full diagnostic.
2. **Where does the mobile gate live (`Xs` or `SmAndDown`)?** — **`md` boundary.** Anything below `md` (960px) is mobile chat. The gate is encoded in the `d-md-*` CSS class names, not in any C# field. iPad portrait (768) gets mobile.
3. **How do we handle visualizations on mobile?** — **Agent-generated markdown summaries.** Every visualization render tool (`render_chart`, `render_graph`, `render_path`, `render_table`, `render_data_table`, `render_timeline`, `render_infobox`) accepts a `mobileSummary` parameter and writes a 3-6 bullet markdown summary alongside the structured visual. The frontend shows the visual on `≥md` and the markdown on `<md` via the same CSS class pattern.
4. **Can `mobileSummary` be made `[Required]` in the LLM tool schema?** — **Optional with strong instructions.** The parameter has a default of `""` so existing C# callers (notably the unit tests in `ComponentToolkitTests.cs`) keep working. The parameter description says *"REQUIRED for mobile users... never leave it null"* and the system prompt has a hard rule: *"ALWAYS POPULATE mobileSummary on every visualization render tool"*. Verified live: the agent reliably populates it.
5. **Does the theme switcher need to respect system dark mode?** — **Yes, via `WatchSystemDarkModeAsync`.** The canonical `MudThemeProvider @ref` pattern handles both initial detection (`GetSystemDarkModeAsync` in `OnAfterRenderAsync`) and live changes (`WatchSystemDarkModeAsync` callback).

## Relationship to other design docs

- Supersedes nothing — this is the first mobile-focused design doc.
- Touches the Ask chat feature ([`Ask.razor`](../../src/StarWarsData.Frontend/Components/Pages/Ask.razor)), which is described at a feature level in the project-level agent and toolkit docs ([ADR-002 AI Agent Toolkits](../adr/002-ai-agent-toolkits.md)). The new `mobileSummary` parameter on every visualization render tool is a backend API change that future work on `ChartToolKit` should preserve.
- Touches the Galaxy Map ([Design 004 Galaxy Map Architecture](004-galaxy-map-architecture.md), [Design 006 Galaxy Map Timeline Mode](006-galaxy-map-timeline-mode.md)) only by hiding it from the mobile drawer — the desktop architecture is untouched.
- **No new ADR-004 entries.** This work introduced zero `@media` CSS, zero raw HTML deviations from MudBlazor primitives. Everything is built from the framework's pre-generated CSS utility classes and MudBlazor components. [ADR-004 MudBlazor Deviations](../adr/004-mudblazor-deviations.md) is unaffected.
- The faction theme switcher (Stream 4) introduces two new files in `Theming/` ([`SwTheme.cs`](../../src/StarWarsData.Frontend/Theming/SwTheme.cs) and [`Themes.cs`](../../src/StarWarsData.Frontend/Theming/Themes.cs)) with six `MudTheme` instances built from `MudBlazor.PaletteLight` and `MudBlazor.PaletteDark`. No theming framework deviation. The picker UI is wired into [MainLayout.razor](../../src/StarWarsData.Frontend/Components/Layout/MainLayout.razor) via the canonical `MudThemeProvider @ref` + `@bind-IsDarkMode` + `WatchSystemDarkModeAsync` pattern.

## Appendix: MudBlazor primitives used (what actually shipped)

All of these are MudBlazor v9.2.0 (the version pinned in the csprojs). None of them require custom CSS or a separate `@media` query in our codebase.

### Visibility (the canonical pattern)

- **`d-none` / `d-block` / `d-flex` / `d-inline-flex`** — base display utility classes from [`_display.scss`](https://github.com/MudBlazor/MudBlazor/blob/v9.2.0/src/MudBlazor/Styles/utilities/layout/_display.scss). Mobile-first.
- **`d-{breakpoint}-{display}` variants** — MudBlazor generates a full set: `d-sm-*`, `d-md-*`, `d-lg-*`, `d-xl-*`, `d-xxl-*`, where `*` is one of `none / inline / inline-block / block / table / table-row / table-cell / flex / inline-flex / contents`. The `md` variants are the ones we use almost exclusively because the mobile gate is at the `md` boundary.
- **The two patterns we use everywhere:**
  - `class="d-none d-md-flex"` — desktop-only flex container (hidden on mobile).
  - `class="d-flex d-md-none"` — mobile-only flex container (hidden on desktop).
  - Same with `d-block`/`d-md-block` for non-flex elements, `d-inline-flex`/`d-md-inline-flex` for buttons that need inline-flex display.

### Dual-render pattern for elements that need different layouts

Used when an element needs different *children* or *binding state*, not just different visibility (e.g. the `MudSwitch` controls in `ContinuityFilter` need different `Label` strings). Render the component twice, both bound to the same backing field, wrapped in paired visibility classes:

```razor
<div class="d-none d-md-flex">
    <MudSwitch T="bool" Value="_canonEnabled" ValueChanged="OnCanonChanged" Label="Canon" />
</div>
<div class="d-flex d-md-none">
    <MudSwitch T="bool" Value="_canonEnabled" ValueChanged="OnCanonChanged" Label="C" />
</div>
```

State stays in sync because both bind the same field. CSS handles visibility. No `_isMobile` field needed.

### Layout primitives that already work

- **`MudDrawer Variant="DrawerVariant.Responsive"` (default)** — auto-collapses to temporary overlay below `Breakpoint.Md`. Out of the box; nothing to do.
- **`MudGrid` with `xs`, `sm`, `md` `MudItem`s** — responsive grid columns. Used in the Ask empty-state example list (`xs="12" sm="6" md="4"` collapses to single column on phones automatically).
- **`MudThemeProvider Theme="@_theme" @bind-IsDarkMode="_isDarkMode" @ref="_mudThemeProvider" ObserveSystemDarkModeChange="true"`** — root theming component. Setting `Theme` to a new `MudTheme` instance regenerates the `--mud-palette-*` CSS variables at the document root, reskinning the entire app instantly via CSS variable inheritance. Used by the faction theme switcher.
- **`_mudThemeProvider.GetSystemDarkModeAsync()`** + **`WatchSystemDarkModeAsync(callback)`** — the canonical pattern (per the MudBlazor docs example) for syncing dark mode to the OS preference. Called from `MainLayout.OnAfterRenderAsync(firstRender)`.

### Backend primitives for the visualization mobile fallback

- **`mobileSummary` parameter** on every visualization render tool ([ChartToolKit.cs](../../src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs)). Optional with default `""` for backwards compatibility, with a strong description string and a system prompt rule reinforcing it as required.
- **`MobileSummary` field** on every visualization descriptor ([Ask.cs](../../src/StarWarsData.Models/AI/Ask.cs)). Nullable string. Carries the agent's bullet-point markdown summary from backend to frontend.
- **`MudMarkdown Value="@Descriptor.MobileSummary"`** in each `Ask*View` mobile branch — renders the agent's markdown via the `MudBlazor.Markdown` package the project already uses for text answers.

### Primitives we explicitly chose **not** to use

- **`MudHidden Breakpoint="..." Invert="true"`** — silently broken in our Blazor Server interactive setup. See [Attempt 1](#attempt-1-mudhidden-with-invert) above. The non-inverted form would also work for our use cases but adds a Blazor component round-trip for something pure CSS handles cheaper.
- **`MudBreakpointProvider` cascade** — silently broken in our setup. See [Attempt 2](#attempt-2-mudbreakpointprovider-cascade). Worth re-trying on a future MudBlazor upgrade.
- **`IBrowserViewportObserver` subscriptions on visibility-deciding components** — works but introduces state duplication, race conditions on first paint, and Blazor lifecycle complexity. See [Attempt 3](#attempt-3-manual-viewport-observer-with-cascading-value). The only place we use `IBrowserViewportService` now is via `MudThemeProvider`'s built-in `WatchSystemDarkModeAsync` for OS dark mode preference watching, which is a different concern from layout visibility.
- **`MudTable Breakpoint="Breakpoint.Sm"`** for responsive card-stacking — the original draft proposed this for tables on mobile. The actual implementation gates whole pages via the Explore-section drawer hide, and replaces visualization tables with markdown summaries via the `mobileSummary` field. The MudTable card-stacking primitive is a fine fallback if a single table page needs to be revived for mobile in the future, but no current page uses it.
