# Feature Specification: SP-4 Wookieepedia Article Modal

**Feature Branch**: `002-sp4-wookieepedia-modal`

**Created**: 2026-05-23

**Status**: Draft

**Input**: User description: "we need a new capability, using speckit - can you add a new feature where the agent (SP-4) is able to open a modal popup window and show a wookiepedia article's content inside it. We do something similar already on the galaxy-map page where if an event is clicked, we load the content of the article. Though I think something which might be better for v1 is actually an iframe using the wookiepedia rest API to only grab article content, if we load the whole page, theres a lot of bloat on wookiepedia website. We only want the article body to be rendered in a mudblazor modal/popup. This would be a fantastic addition to SP-4s capability to be a copilot agent for the user"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ask SP-4 to surface a source article (Priority: P1)

A user is exploring the site (anywhere SP-4's copilot sidebar is available — the galaxy map, the timeline, the knowledge graph, Ask, etc.) and wants to read the underlying Wookieepedia article for the subject under discussion, without losing their place on the current page. They type a natural-language request into SP-4: "show me the Wookieepedia article for Coruscant" / "open the article for Darth Maul" / "pop open Battle of Yavin". SP-4 opens an in-app modal whose body contains the article — text, headings, infobox, images, tables — but **not** the surrounding website chrome, ads, navigation bars, comments, or related-page rails that normally fill the Wookieepedia page. SP-4 also writes one short confirmation sentence in the sidebar telling the user what it did.

**Why this priority**: This is the entire value of the feature. Without it there is no MVP. SP-4 today can read and describe what the user is looking at and can drive certain pages, but cannot show the user the source material it is grounding its answers in. Giving SP-4 a "show me the article" verb closes the loop between "the assistant cited this" and "the user can read it" without forcing a context switch.

**Independent Test**: From any page that mounts the copilot sidebar, type a request matching one of the example phrases above. Verify (a) the modal opens, (b) the body content is the article only — no left navigation, no ads, no comments, no footer rails — (c) SP-4 emits exactly one short confirmation sentence in the sidebar, (d) closing the modal returns the user to the page they were on with no navigation having occurred.

**Acceptance Scenarios**:

1. **Given** the user is on `/galaxy-map` with the copilot sidebar open, **When** the user types "show me the Wookieepedia article for Coruscant" and submits, **Then** a modal opens showing the Coruscant article body (with images, headings, infobox), the modal contains no Fandom site chrome, and SP-4 writes one confirmation sentence such as "Opened the Wookieepedia article for Coruscant." in the sidebar.
2. **Given** the user is on `/ask` and the copilot sidebar is open, **When** the user types "open the article for Darth Maul", **Then** the modal opens with the Darth Maul article body. The page underneath is unchanged.
3. **Given** the user has the article modal open for Coruscant, **When** the user clicks the close affordance, **Then** the modal closes, the user remains on the page they were on, and no browser navigation occurs.
4. **Given** SP-4 has just opened an article modal, **When** the user types a follow-up request that opens a *different* article ("now show me Tatooine"), **Then** the existing modal is replaced by the new article — no stacked modals.
5. **Given** the user asks for an article whose title SP-4 cannot resolve to a real Wookieepedia page ("show me the article for Bothan"), **When** the request is submitted, **Then** no modal opens and SP-4 narrates the failure in prose (e.g. "I couldn't find a Wookieepedia article titled 'Bothan' — did you mean 'Bothawui'?").

---

### User Story 2 - Read the article comfortably and click through if needed (Priority: P2)

Once the modal is open, the user needs to be able to scroll through the article comfortably, follow links inside it that lead to other Wookieepedia pages (without those replacing the current view), and — when they want the full styled Wookieepedia experience (talk pages, edit history, related media) — open the source page on Wookieepedia in a new browser tab.

**Why this priority**: The modal is worthless if it is hard to read or if it traps the user inside a degraded view of the article. Comfortable reading + an obvious escape hatch are table stakes for the surface to be trustworthy.

**Independent Test**: With an article modal open, scroll the body, click an internal article link, and click the external "Open on Wookieepedia" affordance. Verify the modal is scrollable independently of the page underneath, internal links either open in a new tab or load a new article in the same modal (consistent behaviour, see assumptions), and the external affordance opens the canonical Wookieepedia URL in a new browser tab.

**Acceptance Scenarios**:

1. **Given** an article modal is open for a long article (e.g. Coruscant), **When** the user scrolls the modal content, **Then** the modal body scrolls independently of the page underneath and the modal does not close.
2. **Given** an article modal is open, **When** the user clicks the "Open on Wookieepedia" affordance, **Then** the full Wookieepedia page for the same article opens in a new browser tab and the modal remains open in the current tab.
3. **Given** the modal is open on a mobile viewport (narrower than 960px), **When** the modal renders, **Then** it occupies the full viewport (fullscreen behaviour) so the article body is comfortably readable on a phone.
4. **Given** an article modal is open, **When** the user presses Escape or clicks outside the modal (per the application's standard modal dismissal pattern), **Then** the modal closes.

---

### User Story 3 - Continuity-aware article selection (Priority: P3)

The site already has a global filter for continuity (Canon vs Legends — see the continuity colour convention in [CLAUDE.md](../../CLAUDE.md)). When the user has Legends continuity active and asks SP-4 to "show me the Coruscant article", they expect to see the Legends variant of the article, not the canon one. The two variants live at different URLs on Wookieepedia (the Legends variant uses a `/Legends` suffix on the page title). SP-4 must respect the active continuity when resolving the article.

**Why this priority**: Continuity is a first-class user choice on this site. A user who has spent the session reading Legends content and then sees a canon-only article pop open is being surprised by a viewport change they did not initiate — exactly the kind of friction Design-041 calls out as a non-goal. This needs to feel correct to be trusted, but the feature ships value to a canon-default user even without it, so it is P3 not P1.

**Independent Test**: Set the global continuity filter to Legends, ask SP-4 to open an article for an entity that has both variants. Verify the Legends variant loads. Switch back to Canon, repeat with the same entity, verify the Canon variant loads. With the filter set to "Both", verify the Canon variant is the default and there is an in-modal affordance to switch to the Legends variant.

**Acceptance Scenarios**:

1. **Given** the global continuity filter is set to Legends, **When** the user asks SP-4 to open the Coruscant article, **Then** the modal loads the Legends variant of the Coruscant article.
2. **Given** the global continuity filter is set to Canon, **When** the user asks SP-4 to open the Coruscant article, **Then** the modal loads the Canon variant.
3. **Given** the global continuity filter is set to Both, **When** the user asks SP-4 to open an article that has both variants, **Then** the modal loads the Canon variant by default and shows a "Switch to Legends" affordance the user can click to swap to the Legends variant without closing the modal.

---

### Edge Cases

- **Article does not exist on Wookieepedia.** When SP-4 cannot resolve the requested name to a real article title (e.g. a typo, a fictional entity that has no page, an entity that exists only in our local KG and not on Wookieepedia), no modal opens and SP-4 narrates the failure with the next-best suggestion if one is available.
- **Article loads slowly or fails to load entirely.** While the modal body is fetching the article, the modal is visible with a loading indicator. If the article ultimately fails to load (network error, Wookieepedia 5xx), the modal shows a clear error state with a "Try again" affordance and a direct link to the canonical Wookieepedia URL.
- **User navigates within the host application while the modal is open.** When the user clicks an in-app link in the sidebar or page underneath (e.g. an entity-link to `/graph-explorer/{pageId}`), the modal closes so it does not occlude the new page. Navigation is not blocked by the modal.
- **User opens a second article via SP-4 while a modal is already open.** The existing modal's content is replaced by the new article — never stacked. SP-4's confirmation sentence covers the swap ("Switched to the Tatooine article.").
- **Continuity filter changes while a modal is open.** The modal does not auto-reload to the other continuity variant. The user closes and re-asks for the article if they want to switch. (Auto-reloading on filter change would be surprising mid-read.)
- **Mobile viewport with very small screens.** The modal renders fullscreen below 960px viewport width so the article body is comfortable to read on a phone.
- **The article body contains an embedded video / interactive widget that fails inside the modal context.** Acceptable degradation — the modal is for reading, not for media playback. The "Open on Wookieepedia" affordance is always available for users who need the full interactive experience.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: SP-4 MUST expose a tool — global to the SP-4 sidebar, not scoped to any one host page — that opens an in-app modal whose body contains the Wookieepedia article body for a requested subject.
- **FR-002**: SP-4 MUST be invocable for this verb in plain English from any page that mounts the copilot sidebar; the user MUST NOT need to be on a specific page for the verb to be available.
- **FR-003**: The modal body MUST contain only the article content — running prose, headings, infobox, images, tables, internal article links — and MUST NOT contain the surrounding Wookieepedia site chrome (top navigation bar, left sidebar, right rail, comments, footer, related-page rails, advertising).
- **FR-004**: The modal MUST have a clearly visible close affordance, MUST be dismissible by the application's standard modal-dismiss patterns (escape key, click outside, dedicated close button), and MUST NOT trap focus inside it past dismissal.
- **FR-005**: The modal MUST have an "Open on Wookieepedia" affordance that opens the canonical (full-chrome) Wookieepedia URL for the same article in a new browser tab without closing or disrupting the modal.
- **FR-006**: The modal MUST display a clear loading state while the article body is being fetched, and a clear error state with a retry affordance if the fetch fails or the article cannot be reached.
- **FR-007**: After successfully invoking the tool, SP-4 MUST emit exactly one short, plain-language confirmation sentence in the sidebar (e.g. "Opened the Wookieepedia article for Coruscant.") so the user is never surprised by a modal opening without context.
- **FR-008**: When the user requests an article whose title cannot be resolved to a real Wookieepedia page, the system MUST NOT open the modal, and SP-4 MUST narrate the failure in prose, ideally suggesting a closer match if one is available.
- **FR-009**: Only one article modal MUST be open at any time. If SP-4 is asked to open a second article while the first is still open, the existing modal's content MUST be replaced by the new article rather than stacking a second modal on top.
- **FR-010**: The modal MUST respect the application's mobile responsiveness conventions: on viewports narrower than 960px the modal MUST render fullscreen so the article body is comfortably readable on a phone-sized screen.
- **FR-011**: When the user navigates within the host application (clicks an in-app link, switches pages), the modal MUST close so it does not occlude the new view.
- **FR-012**: When the user has the global continuity filter set to Legends, the modal MUST load the Legends variant of the requested article (using the Wookieepedia convention of a `/Legends` page-title suffix). When set to Canon, the modal MUST load the Canon variant. When set to Both, the modal MUST default to the Canon variant and MUST provide an in-modal affordance to switch to the Legends variant in place.
- **FR-013**: SP-4 MUST treat this tool as a non-destructive UI action — the modal opens immediately on the call without an intermediate confirmation step, consistent with the existing page-control verbs documented in Design-041.
- **FR-014**: The tool MUST NOT be usable for destructive or persistent actions; it strictly opens a read-only view of an existing public article. There is no editing surface, no commenting surface, and no path from the modal to any write operation against the site's data.

### Key Entities *(include if feature involves data)*

- **Article Reference**: A reference to a Wookieepedia article. Has either an internal page identifier (preferred — deterministic, drawn from the site's existing knowledge graph) or a free-text title (fallback — used when no internal identifier is known). Optionally has a continuity qualifier (Canon vs Legends) determined from the active global filter at the moment the request is made.
- **Modal Session**: The user-visible state of having exactly zero or one article modal open. Carries the currently-displayed Article Reference and the modal lifecycle state (loading, ready, error, closed). The Modal Session is single-instance — never more than one open simultaneously.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From a cold page load, a user can open an article modal via SP-4 in plain English in under 10 seconds (the round-trip latency budget covers SP-4's reasoning + the article fetch + render).
- **SC-002**: On a desktop viewport, the visible content area of the modal devotes at least 85% of its pixels to article content; the surrounding chrome (modal frame, header, footer, close affordance, "Open on Wookieepedia" button) consumes no more than 15%.
- **SC-003**: When the user requests an article that exists on Wookieepedia, the modal opens with the correct article body in at least 95% of attempts that complete without a network failure. (The remaining ≤ 5% covers title-resolution misses where the user's phrasing genuinely cannot be matched to a real article — those produce a narrated failure, not a wrong article.)
- **SC-004**: Users on a phone-sized viewport can read the full article body without horizontal scrolling and without the modal occluding less than the full screen.
- **SC-005**: A user who has the global continuity filter set to Legends never sees the Canon variant of an article opened by SP-4 without an explicit switch-affordance interaction.
- **SC-006**: SP-4's confirmation sentence is short — never longer than one sentence — so users learn the verb is "low-noise" and trust it. (Verifiable by reviewing any 20 production transcripts of the verb firing and counting confirmation-sentence length.)
- **SC-007**: No user-reported regression of the existing copilot sidebar features (envelope-based reading per Design-022, page-control verbs per Design-041) attributable to this feature's introduction within the first two weeks post-launch.

## Assumptions

- The user has internet connectivity sufficient to load Wookieepedia article content directly from Wookieepedia's servers. The feature does not cache article bodies and does not work offline. (Consistent with the rest of the site's runtime; the local DB caches *structured* data, not source-article HTML for runtime display.)
- The user already trusts the copilot sidebar (Design-022, Design-041); this feature inherits its UX conventions (confirmation sentence, breadcrumb, single-circuit scope).
- The "Open in modal" affordance on existing wiki citations elsewhere in the UI (e.g. the Sources card on `/ask`, in-prose entity links) is a **stretch goal** explicitly deferred from v1. v1 ships with the SP-4-driven path only; user-initiated triggers can come in a follow-up if v1 proves valuable.
- The Wookieepedia article body, fetched via a public body-only article endpoint Wookieepedia exposes for embedding, is acceptable for the modal use case as-is — no additional rewriting of internal links, no image-proxying, no style-overrides beyond what is needed for the modal frame to look consistent with the site's visual language.
- Continuity awareness applies only to entities that genuinely have a Legends variant. For entities that exist only in Canon (post-2014 material) or only in Legends, the request resolves to whichever variant exists. The feature does not require a comprehensive map of which entities have both.
- The feature is governed by Design-041's non-goals: SP-4 still narrates one short sentence per action, still cannot take destructive actions, still cannot drive pages it isn't on. Opening an article modal is a UI action available everywhere the sidebar is, which is a deliberate broadening of Design-041's per-page-scoped action model and will be recorded in a new design doc as part of the planning phase.
- Production deployment requires no schema changes to the knowledge graph, no new ETL phase, no migration. The feature is purely additive on the runtime path.
- The user-facing copy in the modal (footer text, source attribution, switch-continuity affordance) MAY be refined during implementation without re-clarification; the spec fixes intent, not exact wording.
