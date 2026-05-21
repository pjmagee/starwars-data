# ADR-NNN: Title Case Title

**Status:** Proposed
**Date:** YYYY-MM-DD
**Decision maker:** Patrick Magee
**Related:** [ADR-XXX Short Title](xxx-slug.md), [Design-YYY Short Title](../design/yyy-slug.md)

<!--
Header rules (do not delete this comment until the doc is filled in):
- Filename: NNN-kebab-case-title.md, NNN = next free 3-digit number in eng/adr/.
- H1 mirrors the prefix exactly: `# ADR-NNN: Title`.
- Status leading token MUST be one of: Proposed | Accepted | Implemented |
  Partially Implemented | Reference | Superseded | Abandoned | Rejected.
- Date is the doc's first-write date in ISO-8601, not today's date for an old doc.
- Free-form clarification AFTER the leading Status token is allowed and encouraged.
- Use `**Related:**` (not Companion docs / Cross-refs / See also).
- When superseding or being superseded, add the explicit field below.
-->

## Context

Describe the situation forcing a decision. What's the system, what's the problem, what constraints are in play? Reference concrete files, services, or prior decisions — bare paths preferred over abstract names.

### Options considered

If multiple options were weighed in real depth, name them here. Each option gets a brief paragraph or list. Keep it factual; the verdict goes in *Decision*, not here.

## Decision

The chosen option, stated declaratively in one sentence, then expanded. Be specific enough that a reader can implement to spec without asking follow-ups: name the collection, the parameter, the endpoint, the lifecycle, the gate.

## Alternatives considered

Brief notes on options that lost and why. One bullet per option, ending with the concrete reason for rejection (cost, scope, regression, blocked by X).

## Consequences

What this decision causes — new code paths, new constraints on callers, what becomes easy and what becomes hard. Include the bad consequences honestly, not just the good ones.

## Revisit when

Mandatory. Concrete, observable triggers that would make this decision worth re-opening. Examples:

- A specific library adds the feature that's currently missing.
- A specific scale threshold is crossed (e.g. ApiService scaled to N instances).
- A new requirement appears that the rejected option would have served better.

Vague aspirations ("when we have more time", "if performance becomes a problem") are not revisit triggers.
