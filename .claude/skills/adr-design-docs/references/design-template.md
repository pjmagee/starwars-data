# Design-NNN: Title Case Title

**Status:** Proposed
**Date:** YYYY-MM-DD
**Author:** Patrick Magee + Claude
**Related:** [Design-XXX Short Title](./xxx-slug.md), [ADR-YYY Short Title](../adr/yyy-slug.md)

<!--
Header rules (do not delete this comment until the doc is filled in):
- Filename: NNN-kebab-case-title.md, NNN = next free 3-digit number in `specs/` (eng/design/ retired in constitution v2.0.0; this template now describes the `specs/[NNN]-[slug]/spec.md` header format for historical/migrated design narratives — new feature work uses /speckit-specify).
- H1 mirrors the prefix exactly: `# Design-NNN: Title`.
- Status leading token MUST be one of: Proposed | Accepted | Implemented |
  Partially Implemented | Reference | Superseded | Abandoned | Rejected.
  (Designs usually go Proposed → Implemented / Partially Implemented.)
- Date is the doc's first-write date in ISO-8601, not today's date for an old doc.
- Free-form clarification AFTER the leading Status token is allowed and encouraged —
  use it to record ship date, commit refs, and which phases are live vs deferred.
- Use `**Related:**` (not Companion docs / Cross-refs / See also).
- When superseding or being superseded, add the explicit field below the Related line.
-->

## Problem

State the concrete pain point. What does today look like that this design needs to change? Cite files, services, collections, or user-facing surfaces by name. Numbers and live counts ground the doc — vague pain points produce vague designs.

## Goals

Bulleted list of the outcomes this design must achieve. One bullet per outcome. Each should be specific enough to be testable.

## Non-goals

Bulleted list of things explicitly out of scope. Calling them out here prevents scope creep and saves future readers from wondering why an obvious-looking thing wasn't built.

## Design

The body of the doc. Sub-section it however the design needs (numbered sections per phase, per component, per collection — whatever fits). Be specific about names: collection names, parameter names, endpoint paths, file paths.

When the design has multiple phases, label them clearly (`### Phase 1: …`) so the *Status* field can later reference them precisely ("Implemented (Phases 1–2); Phase 3 deferred").

## Alternatives considered

Brief notes on options that lost. One bullet per option, ending with the concrete reason for rejection. This is the same rigour as on an ADR — designs without an *Alternatives* section read as "this was the only thing we thought of", which is rarely the truth.

## Open questions

Bulleted list of things still being decided, with a proposed default for each. When the design ships, either delete the resolved entries or move them to a *Resolved questions* note. A doc with stale open-questions is misleading.

## Revisit when

Mandatory. Concrete, observable triggers that would make this design worth re-opening. Examples:

- Usage data shows the deferred phase is now needed.
- A specific cost threshold is crossed.
- A library or upstream service adds the capability we worked around.

Vague aspirations are not revisit triggers.
