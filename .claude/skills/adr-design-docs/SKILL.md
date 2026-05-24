---
name: adr-design-docs
description: Author or update a numbered ADR under `eng/adr/`, or update the header/status of a historical design narrative migrated to `specs/[NNN]-[slug]/spec.md`. Enforces the shared schema — filename pattern, H1, required header fields, the fixed `Status` enumeration, ISO-8601 dates, the canonical `Related:` link list. Trigger when the user says "write an ADR", "update the status of …", "supersede …", or is editing a file under `eng/adr/`. For NEW feature design narratives, use `/speckit-specify` (spec-kit) instead — `eng/design/` was retired in constitution v2.0.0 (2026-05-24).
---

# ADR Authoring (and historical design-doc maintenance)

> **2026-05-24 v2.0.0 migration**: `eng/design/` was retired. The 45 historical design docs were migrated en masse to `specs/[NNN]-[slug]/spec.md` preserving their numbering. This skill remains canonical for **ADR authoring** and for **updating the headers/status of the migrated design docs in `specs/`**. New feature design narratives should use the spec-kit workflow (`/speckit-specify` → `/speckit-plan` → `/speckit-tasks` → `/speckit-implement`). The "Design header" template below applies to the migrated docs' `spec.md` files.

The repo treats `eng/adr/` and `specs/[NNN]-[slug]/spec.md` as a **living engineering knowledge base** (see `CLAUDE.md` → *Engineering Docs*). A doc that contradicts the code, or that drifts in shape from its neighbours, is a bug. This skill is the schema those docs MUST follow.

**Do not invent fields, do not coin new `Status` values, do not use a date format other than ISO-8601.** When a real situation does not fit (e.g. a brand-new field is genuinely needed), stop and surface it to the user rather than freelancing — silent inconsistency is the problem this skill exists to fix.

## When to apply

- User asks for a new ADR or design doc — pick the matching template under `references/`.
- User asks to update the `Status` of an existing doc — re-read this skill before editing, then change ONLY the field, keeping the rest of the file intact.
- User asks to mark a doc `Superseded` or `Abandoned` — follow the rules in *Lifecycle transitions* below; never delete the file.
- You are editing any file under `eng/adr/` (or a migrated design-narrative `specs/[NNN]/spec.md`) for any reason — verify the header still conforms.

## File location & filename

| Doc type | Location | Filename pattern |
| --- | --- | --- |
| ADR | `eng/adr/` | `NNN-kebab-case-title.md` |
| Design (migrated 2026-05-24; new feature narratives use spec-kit) | `specs/NNN-kebab-case-title/spec.md` | dir is `NNN-kebab-case-title`, file is always `spec.md` |

- `NNN` is a **3-digit zero-padded** sequential integer. Find the next free number by listing the folder; **never reuse a retired number**, even if the file was deleted or renamed.
- Title is **lower-kebab-case**, no abbreviations beyond what's already common in the repo (`kg`, `etl`, `ui`, `api`, `mcp`, `adr` are fine).
- The H1 inside the file MUST mirror the prefix: `# ADR-NNN: Title Case Title` / `# Design-NNN: Title Case Title`. Title in the H1 is **Title Case**, even though the filename is kebab-case.

## Required header block

Every doc starts with the H1, then a blank line, then a header block of bold-key lines. **Order is fixed.** Omit only fields explicitly marked optional.

### ADR header

```markdown
# ADR-NNN: Title Case Title

**Status:** <one of the Status enum below>
**Date:** YYYY-MM-DD
**Decision maker:** <name>
**Related:** [ADR-XXX Short Title](xxx-slug.md), [Design-YYY Short Title](../design/yyy-slug.md)   <!-- optional -->
**Supersedes:** [ADR-XXX Short Title](xxx-slug.md)                                                <!-- only when applicable -->
**Superseded by:** [ADR-XXX Short Title](xxx-slug.md)                                             <!-- only when applicable -->
```

### Design header

```markdown
# Design-NNN: Title Case Title

**Status:** <one of the Status enum below>
**Date:** YYYY-MM-DD
**Author:** Patrick Magee + Claude            <!-- or whichever humans + agents authored it -->
**Related:** [Design-XXX Short Title](./xxx-slug.md), [ADR-YYY Short Title](../adr/yyy-slug.md)   <!-- optional -->
**Supersedes:** [Design-XXX Short Title](./xxx-slug.md)                                          <!-- only when applicable -->
**Superseded by:** [Design-XXX Short Title](./xxx-slug.md)                                       <!-- only when applicable -->
```

Field rules:

- **Status** — must start with one of the eight enum values below (see *Status enum*). Free-form clarification after an em-dash is allowed and encouraged: `**Status:** Implemented — 2026-04-06; the X collection plus Y view are live, see commit abc1234.`
- **Date** — ISO-8601 (`YYYY-MM-DD`) only. The date the doc was **first written**, NOT the date of the most recent status change (that goes in the Status clarification). Never `(2026-04-25)`, never `April 25 2026`, never `2026/04/25`.
- **Decision maker** (ADR) / **Author** (Design) — short attribution string. Both human and agent collaborators should be listed when both contributed.
- **Related** — single canonical name. **Do not** introduce variants (`Companion docs`, `Cross-refs`, `Context companion`, `See also` — all forbidden, even though older docs use them). When updating an existing doc with a non-standard variant, rename it to `Related:` as part of the edit.
- **Supersedes / Superseded by** — only present when one doc replaces another (see *Lifecycle transitions*). These are mandatory when the relationship exists.
- Link format: `[Short Title](relative-path.md)` — use a human-readable short title, not the bare filename. Cross-folder paths: design→adr is `../adr/`, adr→design is `../design/`.

## Status enum

These are the **only** allowed leading tokens for `Status:`. Pick the one that matches reality at the moment of the edit.

| Value | Meaning | Notes |
| --- | --- | --- |
| `Proposed` | Doc exists, no code. | Used for both ADRs (decision not yet ratified) and designs (no implementation yet). |
| `Accepted` | ADR ratified; not necessarily implemented yet. | ADR-only. Designs go straight to `Proposed` → `Implemented`. |
| `Implemented` | All described behaviour is shipped on `main` and verified. | Strongly prefer to append a date and a one-line code pointer (file/commit). |
| `Partially Implemented` | Some phases shipped, others not. | Spell out which phases are live and which are deferred, with absolute dates. |
| `Reference` | Descriptive snapshot of the current state, not a forward-looking decision. | Use sparingly — most docs should pick a forward state instead. |
| `Superseded` | Replaced by a newer doc. | `**Superseded by:**` field is mandatory. Leave the body intact for historical context. |
| `Abandoned` | Explored, not pursued, no successor doc. | Briefly explain why in a leading clarification. |
| `Rejected` | An ADR option that was considered and dismissed. | ADR-only; rare — most rejected options live inline under *Alternatives considered*. |

Anything else (`Shipped`, `Done`, `Live`, `Partially shipped`, `Known limitation`, `WIP`, lowercase `abandoned`) is **non-conformant** and must be normalised. When you encounter one while editing, fix it in the same edit.

## Section skeleton

Pick the matching template under `references/` — copy it verbatim, then fill in. Do not reshuffle the top-level headings.

- ADR → [references/adr-template.md](references/adr-template.md). Sections: *Context* → *Decision* → *Alternatives considered* → *Consequences* → *Revisit when*.
- Design → [references/design-template.md](references/design-template.md). Sections: *Problem* → *Goals* → *Non-goals* → *Design* → *Alternatives considered* → *Open questions* → *Revisit when*.

*Revisit when* is mandatory in both — a decision with no revisit trigger is a decision you can't audit later. It's a list of concrete, observable conditions (e.g. "ApiService scaled to multiple instances"), not vague aspirations.

## Lifecycle transitions

These are the moments where header consistency breaks down most often. Follow these rules exactly.

### Proposed → Implemented

- Change `Status:` leading token to `Implemented`.
- Append a clarification: `Implemented — YYYY-MM-DD. <one-line pointer to the code or commit>`.
- **Do not** alter the `Date:` field — that's the doc's birthday, not the ship date.
- If the doc is in `specs/[NNN]/spec.md` (a migrated design narrative) and contains an `## Open questions` section that the implementation resolved, either delete the answered questions or convert them to a *Resolved questions* note at the bottom of that section.

### Implemented → Partially Implemented

- Used when later editing reveals a phase was deferred. Be specific about which phases are live and which are not, with dates.

### Doc A supersedes Doc B

- In **B**: set `Status:` to `Superseded`, add `**Superseded by:** [Doc A …](path)`. **Do not delete B's body** — the rationale captured there is still useful for future readers.
- In **A**: add `**Supersedes:** [Doc B …](path)` to the header.
- Add a one-line note at the very top of B (after the header block) explaining why it was superseded.

### Doc is abandoned

- Set `Status:` to `Abandoned`, with a leading clarification: `Abandoned — YYYY-MM-DD. <brief reason>`.
- Leave the body intact. Do not rename the file or change its number.

## Verifying before you save

Run through this checklist before the edit lands:

1. Filename matches `NNN-kebab-case-title.md`, with `NNN` = the next free number.
2. H1 is `# ADR-NNN: …` or `# Design-NNN: …` with Title Case after the colon.
3. `Status:` leading token is one of the eight enum values, case-sensitive.
4. `Date:` is `YYYY-MM-DD` and is the doc's first-write date, not today's date for an old doc.
5. Related-doc field is named `Related:` (rename any `Companion docs:` / `Cross-refs:` / `Context companion:` you find).
6. *Revisit when* section is present and contains at least one concrete condition.
7. If the H1 number, the filename number, and a `Supersedes`/`Superseded by` reference number all need to match — they do.
8. No new `Status` value has been invented. If reality genuinely doesn't fit the enum, raise it with the user rather than coining one.

## Updating CLAUDE.md when relevant

`CLAUDE.md` references several ADRs and design docs by path; those links are load-bearing. When the new doc **establishes a rule or pattern an agent must follow** (e.g. ADR-004's library-deviation rule, ADR-009's stats-surface carve-out), add a link to it from the relevant section of `CLAUDE.md` in the same change. Reference-only docs (Status `Reference`) don't need a CLAUDE.md mention.
