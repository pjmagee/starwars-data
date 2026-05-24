# ADR-007: Hardening the Holocron pipeline over rewriting it

**Status:** Accepted
**Date:** 2026-04-29
**Decision maker:** Patrick Magee

## Context

Designs [025](../../specs/025-holocron-tool-using-agent/spec.md) and [026](../../specs/026-holocron-orchestration-pattern/spec.md) proposed an architectural pivot for Holocron (the Phase 2 KG enrichment agent — see [ADR-006](006-long-running-ai-workflow-pipelines.md), [Design-018](../../specs/018-kg-enrichments-architecture/spec.md), [Design-020](../../specs/020-holocron-async-pipeline/spec.md)). The framing was:

- Holocron's structured-output extractor was producing low-quality proposals (Aliases stuffed with role strings, edges pointing at wrong-typed nodes, target-substitution hallucinations).
- Each new failure mode landed a new sieve layer in the consolidator.
- Replace the structured-output approach with a **tool-using agent** whose tool calls would be the audit trail (Design-025), running over a **concurrent fan-out of agent-host executors** (Design-026).

The proposed v2 architecture was a substantial rewrite: ~7–10 hours of focused implementation per Design-026's estimate, plus the cutover risk of running two extractor implementations side-by-side.

Before committing to the pivot we did a reality-check run on Asajj Ventress and Ahsoka Tano under the existing v1.3.0 pipeline. The audit revealed that the originally-documented failure modes had **already been substantially addressed** by the v1.3.0 sieve layers (template-scoped Phase E, Aliases entity-blocklist, Phase G cross-vector dedup, target-type validation). The **remaining failures were content hallucinations** — target-substitution and co-mention-as-evidence — which are *not* what Designs-025/-026 were architected to fix.

That pulled the rug out from under the rewrite premise. The structural sieve was working; the problem had moved upstream.

## Options Considered

### Option A: Pivot to the tool-using agent (Designs-025 / -026)

Build the tool surface (resolve_entity, find_canonical_label, check_existing_edges, get_template_schema, propose_edge, propose_property, suggest_new_label), wrap a single AIAgent in AIAgentHostExecutor, fan out per-batch, retire the v1.x structured-output extractor.

**Rejected because:**

- The audit data showed the current v1.3.0 sieve was catching the **structural** failure modes (label / type / fieldPath / pair-already-exists) the tool-using agent was designed to prevent. The failure modes that remained — target substitution, co-mention claims, weak inference — would not be fixed by a tool architecture; they're prompt / verifier concerns.
- The remaining v1.x failures were measurable on the Anakin canonical run (49 enrichments, ~1 hallucination + a Titles property leak) and addressable with targeted commits (regex for self-flagged claims, LLM verifier for semantic check, prompt rewrite). Each fix was 30 min – 4 hours and reversible. The rewrite was 7–10 hours and a parallel-implementation risk.
- The synonym-table approach (Design-025 §The label / vocabulary problem) was speculative. We built it, tested it, and explicitly removed it after the user identified it as over-engineering — there is no real signal that the agent was struggling with synonym lookup. The label vocabulary lookup is a 6-line LINQ filter over `FieldSemantics.Relationships`.
- Phase A.1 read tools (`resolve_entity` / `find_canonical_label` / `check_existing_edges` / `get_template_schema`) shipped as a foundation but **are not wired into any agent** — the v1.x extractor uses the original structured-output path unchanged.

### Option B: Targeted hardening — v1.4.0 → v1.7.0 (chosen)

Iterate on the existing pipeline driven by per-proposal audit data. Each version targets a specific failure class:

- **v1.4.0** — hedge-word regex pre-flight + F5 fix (consolidator queries `kg.edges` directly for full pair set instead of relying on the discovery executor's top-K-by-weight cache).
- **v1.5.0** — universal property-value blocklist (Phase G's Aliases-only check generalised to every fieldPath) + LLM evidence verifier as a new executor between consolidator and apply.
- **v1.5.1** — verifier scoped to Adds + Properties only after calibration showed it was over-strict on year-bound inferences. Annotate / FillGap pass through.
- **v1.5.2** — `HolocronMaxNeighborsForContext` 10 → 60 so high-degree nodes don't lose Lifecycle-tagged FillGap candidates.
- **v1.6.0** — `kg.holocron_audits` collection: one row per unique post-dedup proposal with outcome stamped at every pipeline stage. Lifts the silent-rejection black box.
- **v1.7.0** — system prompt rewrite, anti-pattern-driven, ~250 lines → ~130, organised around a per-fact decision workflow.

## Decision

**Option B.** Hardening over rewriting.

## Why this is the right call (with numbers)

The Anakin canonical run (PageId 452390) was the test case for both options. Running it through each version with the audit collection populated gives a direct comparison:

| Pipeline metric | v1.3.0 | v1.6.0 | v1.7.0 (current) |
|---|---|---|---|
| Raw proposals extracted | 352 | 2179 | 752 |
| Unique post-dedup | n/a | 121 | 57 |
| Wrong-target-type rejections | n/a | 10 | **1** |
| Target-substitution rejections | n/a | 44 | **12** |
| Dup-pair rejections | 2 | 14 | 7 |
| Verifier rejections | n/a | 7 | 7 |
| Applied enrichments | 26 | 49 | 18 |
| Hallucinations in applied output | 7+ | 1 | **0** |

The **prompt rewrite alone** (v1.6.0 → v1.7.0) cut wrong-target-type rejections by 90% and target-substitution by 73%. The agent stopped emitting bad proposals at source rather than having them caught downstream.

Specifically delivered on the user's canonical test case:

- `Anakin --[apprentice_of]--> Darth Sidious` FillGap with `fromYear=-19, toYear=4` (Mustafar to Endor) — refining the Phase 1 Lifecycle-tagged bound of `-41 to 4`.
- `Anakin --[has_role]--> Dark Lord of the Sith` Add with same -19 → 4 bounds.
- 18 high-quality enrichments overall, with no hallucinated target-substitutions surviving.

A tool-using-agent rewrite would not have outperformed this on the same data; it would have re-encoded the structural validation in tool calls and still needed a prompt and a semantic verifier for the content failures.

## Consequences

- **The v1.x extractor stays.** `HolocronProposalExtractorExecutor` continues to use the `IChatClient` + structured-output path. No fan-out, no AIAgentHostExecutor multiplicity. The 5-stage workflow becomes 6 stages with the verifier inserted between consolidator and apply.
- **`kg.holocron_audits` is the audit trail.** Per-proposal outcomes per stage are durable in Mongo, queryable, and replace the "black box rejection counter" that motivated the original rewrite premise. Future tuning is data-driven.
- **Phase A.1 read tools (Design-025 §The tool surface) shipped as a foundation but are not wired up.** They're available for any future tool-using agent that wants them; there is currently no caller. Total dead weight is ~600 LOC + 25 tests.
- **The synonym table is gone.** Design-025's `LabelSynonymTable` was prototyped, tested with 14 unit tests, then deleted after the user identified it as solving a problem we don't have. `find_canonical_label` is now a thin LINQ filter over `FieldSemantics.Relationships`.
- **The fan-out architecture is not built.** Design-026's per-batch concurrent `AIAgentHostExecutor` pattern is unimplemented; batches still run sequentially within `HolocronProposalExtractorExecutor`. The throughput trade-off (Anakin: 30 min per run sequential) was acceptable given the quality gains were the actual goal.
- **Designs 025 / 026 are marked superseded** — they contain useful diagnosis but the proposed pivot was rejected. Design-028 (tri-view + Holocron wipe) remains a future direction; the audit-driven KG read-side fixes that landed in v1.7.0 (chip dedup, Holocron Add expansion in graph explorer, labels-endpoint filter, realm/universe alias) prefigure parts of it but the full tri-view UI is not built.

## Revisit when

- The agent's prompt-driven discipline plateaus. If we hit a node where v1.7.0 still produces ≥3 structural hallucinations per run despite tuning, the tool-using architecture's call-time validation may genuinely be needed.
- Throughput becomes the binding constraint (Sidious-class super-hubs taking >2 hours per run). The fan-out concurrency would actually pay off then.
- The label vocabulary growth signal (Design-025 §The vocabulary-growth tool, `kg.label_suggestions`) becomes important. We currently have no first-class mechanism for the agent to flag "I needed a label that doesn't exist".

Until one of those triggers, hardening over rewriting is the policy.
