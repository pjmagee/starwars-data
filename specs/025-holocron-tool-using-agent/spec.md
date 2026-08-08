# Design-025: Holocron as a tool-using agent (vs. structured-output workflow)

**Status:** Superseded — pivot rejected. See [ADR-007](../../eng/adr/007-holocron-hardening-over-rewrite.md). Phase A.1 read-tool foundation was later removed as unwired dead weight (issue #9; lives in git history only); the v1.x structured-output extractor was hardened instead via v1.4.0 → v1.7.0.
**Date:** 2026-04-28
**Author:** Patrick Magee + Claude
**Related:** [Design-018 KG enrichments architecture](../018-kg-enrichments-architecture/spec.md), [Design-020 Holocron async pipeline](../020-holocron-async-pipeline/spec.md), [Design-021 Edge bound provenance](../021-edge-bound-provenance/spec.md), [Design-023 Character roles as edges](../023-character-roles-as-edges/spec.md), [Design-024 Typed NodeBuilders](../024-typed-node-builders/spec.md), [ADR-006 Long-running AI workflow pipelines](../../eng/adr/006-long-running-ai-workflow-pipelines.md), [ADR-007 Hardening over rewriting](../../eng/adr/007-holocron-hardening-over-rewrite.md)

## Outcome (added 2026-04-29)

The pivot proposed below was tested against real audit data and **rejected**. The reality-check on Asajj Ventress and Ahsoka Tano showed v1.3.0's existing sieve had already absorbed most of the originally-documented failure modes (Aliases stuffing, wrong-target-type edges, fieldPath leaks, dup-pair Adds, hedge-claim hallucinations). The remaining failures were content / target-substitution hallucinations, which are prompt-engineering and semantic-verification concerns — not architectural pivot territory.

**What shipped from this design:**

- ~~`Services/AI/Agents/Holocron/Tools/HolocronReadToolkit.cs`~~ — Phase A.1 read tools (`resolve_entity`, `find_canonical_label`, `check_existing_edges`, `get_template_schema`) plus DTOs/tests were removed as unwired dead weight in issue #9; see git history / ADR-007.
- The `LabelSynonymTable` was prototyped, tested, then deleted after the architecture-wide pushback that this was over-engineering (before the rest of the Phase A.1 surface was removed in issue #9).

**What did NOT ship:**

- `propose_edge` / `propose_property` / `suggest_new_label` (the write tools).
- The tool-using `AIAgent` factory.
- The replacement of the v1.x structured-output extractor.
- The `kg.label_suggestions` collection / vocabulary-growth UI.
- Any cutover from the existing five-executor workflow.

**What did the actual hardening look like?** See [ADR-007](../../eng/adr/007-holocron-hardening-over-rewrite.md) for the v1.4.0 → v1.7.0 trajectory: hedge-word regex, F5 fix (full-pair check against `kg.edges`), universal property blocklist, LLM evidence verifier as a new executor, top-K bump, `kg.holocron_audits` collection, system-prompt rewrite. Anakin canonical test now lands `apprentice_of → Sidious -19→4` and `Dark Lord of the Sith` Add with zero hallucinations in surviving output. The path that worked was iterating on the existing pipeline driven by per-proposal audit data, not replacing it.

The diagnosis below — particularly the failure-mode catalogue and the per-failure-mode mapping — remains useful as a record of what was wrong with v1.2.x. The proposed architecture is documented for posterity but is no longer the plan.

---

## TL;DR

The current Holocron is a five-stage workflow that asks one LLM call per chunk-batch to emit a fully-encoded structured-output payload (`annotateEdges`, `fillGapEdges`, `addEdges`, `nodeProposals`). The agent has to do two distinct jobs at once: **(a) extract facts from prose**, and **(b) decide how to encode each fact in our graph schema**. Job (a) is what LLMs are good at; job (b) is a deterministic lookup against `kg.nodes` + `kg.labels` + `FieldSemantics` that we keep handing to the LLM and patching its mistakes with sieve layers.

This design proposes splitting the two jobs:

- The **LLM extracts facts** as language-shaped tuples: *(subject, predicate-prose, object-prose, evidence)*.
- A **tool layer encodes** each tuple deterministically: resolves the object against `kg.nodes`, maps the predicate against a synonym table, and routes to either an edge or a property by construction.

The architecture shifts from a **workflow with LLM-in-the-middle** to a **tool-using agent** whose scratchpad of tool calls is itself the audit trail. Most of the consolidator's current rule logic disappears — failure modes become structurally unreachable rather than caught after the fact.

This is a follow-on to Design-018 (which shipped the enrichments substrate) and Design-020 (which shipped the async workflow). The substrate is fine. The pipeline shape isn't.

## Problem

### What the user is asking for

Three rules, stated by the user on 2026-04-28:

1. Attributes remain attributes.
2. Valid recommended edges to legitimate nodes.
3. No duplicate or near-synonymous edge labels.

These should be trivial to enforce. They aren't, with the current architecture. Below is the concrete record of *why* — drawn from the Asajj Ventress and Anakin Skywalker enhancement runs over the past 48 hours.

### What the user is actually seeing

From the Holocron-added attributes panel for Asajj Ventress (page 453169) after a v1.2.0 run:

- **`Aliases`** (47 sources, 25 values): includes `"bounty hunter"`, `"Sith apprentice"`, `"The Bounty Hunter"`, `"Dark disciple"`, `"Black Sun"`, `"Nightsister"`, `"Padawan of Ky Narec"`, `"Queen of the Nightsisters"`, `"Sister of Dathomir"`. Roughly half of these are roles, factions, or descriptive epithets — not alternate names.
- **`Occupation`**: `"bounty hunter, Warrior"` — duplicates a `has_role → Bounty hunter` edge proposed in the same run.
- **`Primary role(s)`**: `"Warrior, Bounty hunter"` — duplicates `Occupation` AND duplicates the `has_role` edge AND `Primary role(s)` is a **starship** infobox field, not a Character one.
- **`addEdges: has_role → Darth Sidious`**: target type is `Character`, but the canonical label `has_role` declares `ExpectedTargetTypes: ["TitleOrPosition"]`. Wrong-target edge.
- **`addEdges: collaborates_with → Cad Bane`**: `collaborates_with` is canonically scoped to musical collaborations (band/musician); the agent picked it for general-character-interaction.

Each of these failure modes has been triaged and a sieve layer added to the consolidator (target-type validation, template-scoped Phase E, Aliases blocklist, Phase G cross-vector dedup). Every patch addresses *one symptom*. None addresses the underlying split-of-responsibility.

### Why every patch adds another sieve

The consolidator's pre-flight validation block is now ~120 lines covering:

- `IsAnnotateValid` — edge tuple must exist in `kg.edges`.
- `IsFillGapValid` — bounds must be Lifecycle-tagged.
- `IsAddEdgeValid` — pair must not exist; label must be canonical; target type must match `ExpectedTargetTypes`.
- Phase E — fieldPath must be a canonical property.
- Phase E (template-scoped) — fieldPath must be valid for *this template*.
- Phase G (cross-vector dedup) — property values must not overlap with edge target names.
- Aliases blocklist — Aliases values must not resolve to TitleOrPosition / Government / Organization / Religion / Species / Family / MilitaryUnit / CulturalGroup / FanOrganization nodes.

Every single one of these checks exists because the LLM made a category mistake the prompt couldn't reliably prevent. The pattern is:

> Agent emits something semantically wrong → we lose data → user reports it → we add another sieve layer.

The sieve catches more wrongness over time, but the *cost* is that legitimate signal also gets dropped (silently — `preflightRejects++` and the proposal text is gone), and the consolidator's logic now encodes constraints that should be enforced at the point the LLM forms the proposal, not three stages later.

### The label / vocabulary problem

There are 50+ canonical labels in `FieldSemantics.Relationships` plus an additional ~30 inferred from per-type NodeBuilder OnFinalize hooks (Design-024). The agent gets a top-25 slice scoped to its source-type from `kg.labels`. Even with that pruning, three failure modes recur:

1. **Synonym confusion.** The agent picks `member_of` when an `affiliated_with` edge already exists between the same pair. Or `collaborates_with` for a non-music collaboration. There is no single canonical mapping of "general English predicate" → "canonical label" exposed to the agent — it's expected to read 25 labels' descriptions and pick the best match. It picks plausibly, not consistently.
2. **Vocabulary growth.** When the agent has a legitimate insight that doesn't map to any canonical label (e.g. *"X briefly fought alongside Y at Z"* — no existing label captures the *briefly* qualifier), it's silently dropped. There is no feedback loop to grow the vocabulary based on real usage signal.
3. **Per-source-type semantics.** A Character's `affiliated_with → Government` is a faction-membership; a Character's `affiliated_with → Religion` is a member-of-order; a Character's `affiliated_with → Family` is a family-membership. Design-024 partially solved this with per-target-type relabelling on the *Phase 1* side. The Holocron agent doesn't do this and emits flat `affiliated_with` regardless. (Phase G's name-overlap dedup masks this when the property dump and the edge name match, but the underlying label-fragmentation is unsolved.)

### The duplicate-encoding problem

A single fact appears in the corpus in multiple representations:

- The infobox: `Occupation: bounty hunter`
- The prose: *"...worked as a [[bounty hunter]]..."*
- The wikilink: `[[bounty hunter]] → 456298 (TitleOrPosition)`

Phase 1 extracts the infobox into `Occupation` as a property. The Holocron agent reads the prose plus the wikilink and emits *both* a `has_role` edge AND an `Occupation` property AND a `Primary role(s)` property AND an `Aliases` value. **One fact, four encodings.** Each pass through the prompt and the validation pipeline, the agent re-makes the duplication — because the prompt frames each output array as independent and the agent has no awareness of what it has already proposed for the *same fact*.

## Diagnosis: workflow vs. agent

### Where the workflow architecture wins

Design-020's five-executor pipeline (Discovery → Bundling → Extraction → Consolidation → Apply) is **excellent infrastructure** and should not be discarded:

- Resumable through `MongoCheckpointStore` — a 30-minute Anakin run can survive process restarts.
- Per-batch progress tracked in side-channel collections, decoupled from the framework checkpoint's 16 MB ceiling.
- Two-phase apply with quarantine + idempotent unique indexes — no double-writes on resume.
- Proven on Anakin (2520 chunks), Padmé, Mace Windu, and Boost.

The infrastructure works. **The problem is what we're asking the LLM to do inside the Extraction stage.**

### Where the workflow architecture loses

The Extraction executor calls the LLM with structured output:

```
{
  annotateEdges: [...],
  fillGapEdges: [...],
  addEdges: [...],
  nodeProposals: [...]
}
```

This shape requires the LLM to have, in its single forward pass:

- Read 50–200 chunks of prose.
- Identified every fact across them.
- For each fact, decided whether it's an Annotate / FillGap / AddEdges / NodeProposal.
- Picked the canonical label from a 25-item dropdown.
- Resolved chunk-text mentions to PageIds via the inline linked-entities section.
- Avoided proposing the same fact twice in different arrays.
- Avoided proposing a fact that's already encoded in the infobox.
- Avoided proposing a fact whose target type doesn't match the label's `ExpectedTargetTypes`.
- Avoided proposing a property whose fieldPath isn't valid for this template.

That's nine constraints, all in a single zero-shot generation. LLMs are not zero-shot good at constraint-satisfaction over schemas they're shown only in-prompt. Some constraints (like target-type matching) require the agent to *cross-reference its own proposed `toId` against a node lookup* — it can't, because we never gave it a tool to look anything up. The result is plausible-looking output that fails when the consolidator runs it through deterministic code.

### Why "agent with tools" is structurally different

A tool-using agent inverts the responsibility:

- The **agent** thinks in language: *"Asajj worked as a bounty hunter at Dooku's command from 22 BBY."*
- The **tools** convert language to graph operations: `resolve_entity("bounty hunter")` returns `(456298, TitleOrPosition)`. `find_canonical_label("worked as", target_type=TitleOrPosition)` returns `has_role`. `propose_edge(...)` validates and stages.

Three structural wins:

1. **Decisions become deterministic where they should be deterministic.** "Is this an edge or a property?" is a wiki-page existence check, not a judgment call. "What's the canonical label for *worked as*?" is a synonym-table lookup, not a 25-item dropdown the LLM picks blindly.

2. **Tool-call errors become recoverable.** If the agent calls `propose_edge(label="has_role", to=Sidious_Character)`, the tool returns *"rejected: has_role expects target type TitleOrPosition; Sidious is Character. Did you mean apprentice_of?"* The agent self-corrects in the same conversation. With structured output, the same mistake is silent — it's caught downstream by the consolidator and the agent never finds out.

3. **The audit trail is the conversation.** Every tool call is a discrete decision with arguments, return value, and post-condition. There's no "the LLM emitted this and we don't know why" — every encoding has a tool call attached, and every tool call has a justification in the agent's preceding message.

## Proposed architecture

### High-level shape

```
HolocronContextDiscoveryExecutor      ── unchanged
HolocronChunkBundlingExecutor          ── unchanged
HolocronAgentLoop                       ── REPLACES HolocronProposalExtractorExecutor
HolocronApplyExecutor                   ── simplified (consolidator merges in)
```

The five-stage workflow becomes four. Discovery + Bundling stay deterministic. The Extraction + Consolidation stages collapse into a **single agent loop** per batch:

```
for each chunk-batch:
  conversation = new Conversation(systemPrompt + chunkBatch)
  while agent has more to say:
    LLM emits assistant message (free-form reasoning + tool calls)
    each tool call is executed; result returned to conversation
    agent decides next step
  collect "staged" enrichments from this batch
```

Each batch is its own conversation, scoped to that batch's chunks. Cross-batch dedup happens at the apply step (which already has dedup logic for the existing pipeline).

### The tool surface

Six tools, all read-only or write-staged. Tools never write directly to `kg.enrichments` / `kg.edge_enrichments` — they stage proposals into per-batch in-memory state, validated at call-time. Apply runs at the end of the run.

#### Read tools (the agent's view of the graph)

**`resolve_entity(text: string) → { pageId, name, type, wikiUrl } | null`**
Deterministic lookup against `kg.nodes`. First tries exact-name match, then alias match (the existing kg.nodes name index plus name-or-Titles match from the per-type NodeBuilder pass). Returns `null` if no match — the agent then knows this string is *not* a node and (for a property-shaped fact) can use `propose_property` instead.

**`find_canonical_label(predicate_prose: string, source_type: string, target_type: string) → { label, reverse, description, evidence_examples } | null`**
Synonym-table lookup. The synonym table maps natural-language predicates to canonical labels, scoped by source × target type. Examples:

```
("worked as",  Character, TitleOrPosition)  → has_role
("served as",  Character, TitleOrPosition)  → has_role
("apprentice of", Character, Character)     → apprentice_of
("apprentice to", Character, Character)     → apprentice_of
("fought at", Character, Battle)            → participated_in
("led",      Character, MilitaryUnit)       → led
("commanded", Character, MilitaryUnit)      → commanded
("member of", Character, Organization)      → member_of
("member of", Character, Religion)          → member_of_order
("member of", Character, Family)            → member_of_family
```

Returns `null` if no synonym matches. The agent then either (a) skips the fact, or (b) calls `suggest_new_label` (see Write tools).

**`check_existing_edges(from_id: int, to_id: int) → [{ label, fromYear, toYear, ... }]`**
Lists every edge between the pair, in either direction. Replaces the consolidator's "must not appear in candidate lists" check — the agent can decide between `Annotate` (existing edge, refine context) and `propose_edge` (new edge) by querying.

**`get_template_schema(node_type: string) → { properties: [...], relationships: [...], temporalFields: [...] }`**
Returns what `InfoboxDefinitionRegistry.ForTemplate(node_type)` would return — the per-template allow-list of properties + relationships. The agent calls this once per run to know what's valid for the target template.

#### Write tools (stage proposals)

**`propose_edge(from_id, to_id, label, kind: "add"|"annotate"|"fill_gap", from_year?, to_year?, role?, qualifier?, description?, evidence)`**
Single tool covering all three edge operations. Validates synchronously:

- Pair, label, and kind compatibility (e.g. `annotate` requires the edge to exist).
- Target type matches `label.ExpectedTargetTypes`.
- For `add`: pair has no existing connection.
- For `annotate`: edge `(from, to, label)` exists.
- For `fill_gap`: edge exists and bounds are Lifecycle-tagged (Design-021).
- Evidence cites a real chunkId / pageId from the batch context.

Returns either `{ staged: true, proposalId }` or `{ rejected: true, reason }`. The agent sees the rejection and can correct.

**`propose_property(field_path, values, evidence)`**
Stages a property proposal. Validates:

- `field_path` exists in `ForTemplate(target_type).Properties`.
- Property is currently empty in `kg.nodes` (otherwise it would be a contradiction).
- Each value is NOT an entity name resolvable via `resolve_entity` — if it is, the tool returns `{ rejected: true, reason: "value 'bounty hunter' resolves to TitleOrPosition node 456298; use propose_edge(has_role) instead" }`. This kills the Aliases-stuffed-with-roles failure mode at call-time.
- For Aliases specifically: each value passes a "is-this-a-proper-noun-name" heuristic.

#### Vocabulary-growth tool

**`suggest_new_label(predicate_prose, source_type, target_type, justification, evidence) → { suggestion_id }`**
When the agent sees a relationship that doesn't fit any canonical label, instead of silently dropping the fact, it logs a suggestion. Stored in a new `kg.label_suggestions` collection with frequency counters. Admin UI surfaces top-K suggestions by aggregate evidence weight; promoting a suggestion to canonical is a code change to `FieldSemantics`. **This closes the silent-data-loss bug** — the agent's vocabulary discovery is now first-class signal.

### What the agent's conversation looks like

For one chunk-batch, abbreviated:

```
SYSTEM: You are the Holocron. Use the tools to extract facts from the chunks
        and stage them as edges or properties. The tools enforce all schema
        rules — if a tool rejects your call, read the reason and try a
        different encoding.

USER:   Target: Asajj Ventress (453169, Character)
        Chunks (12): [...prose with [[wikilinks]]...]

AGENT:  Reading chunks. The first chunk says "After Ky Narec's death, Asajj
        was found by Count Dooku, who took her as his Sith apprentice."
        Two facts: (1) Ky Narec was Asajj's prior master; (2) Dooku became
        her Sith apprentice.

        For (1):
        > resolve_entity("Ky Narec")
        ← { pageId: 472883, type: Character }
        > find_canonical_label("master of", Character, Character)
        ← { label: "apprentice_of", reverse: "master_of" }
        > check_existing_edges(453169, 472883)
        ← [{ label: "apprentice_of", fromYear: -50, toYear: -39 }]
        Existing edge with bounds. Skipping — already encoded.

        For (2):
        > resolve_entity("Count Dooku")
        ← { pageId: 452893, type: Character }
        > find_canonical_label("apprentice to", Character, Character)
        ← { label: "apprentice_of" }
        > check_existing_edges(453169, 452893)
        ← [{ label: "apprentice_of", fromYear: null, toYear: null }]
        Existing edge, no bounds. Will fill_gap with chunk evidence.
        > propose_edge(from=453169, to=452893, label="apprentice_of",
                       kind="fill_gap", from_year=-39,
                       evidence=[chunkId, "After Ky Narec's death..."])
        ← { staged: true, proposalId: ... }

        Continuing chunks...

        Chunk 4: "Asajj operated as a [[bounty hunter]] for Sidious's
        Confederacy interests during the Clone Wars."
        > resolve_entity("bounty hunter")
        ← { pageId: 456298, type: TitleOrPosition }
        Wikilink resolves — this is a node, not a property.
        > find_canonical_label("operated as", Character, TitleOrPosition)
        ← { label: "has_role" }
        > check_existing_edges(453169, 456298)
        ← []
        New edge.
        > propose_edge(from=453169, to=456298, label="has_role",
                       kind="add", from_year=-22, to_year=-19,
                       evidence=[...])
        ← { staged: true, proposalId: ... }

        I will NOT also propose Occupation: "bounty hunter" — the edge is
        the canonical encoding, and propose_property would reject this
        value because resolve_entity confirmed it's a node.
```

Note what's *absent* from this conversation versus the current architecture:

- No 200-line system-prompt section explaining when to use which array.
- No separate "Canonical edge labels (you MUST pick from)" + "Canonical property fieldPaths (you MUST pick from)" appendices.
- No "remember not to propose the same fact in two arrays" rule.
- No post-hoc consolidator validation — every constraint is enforced at the call site.

The agent's prompt shrinks from ~3500 tokens (the v1.3.0 system + user prompt) to maybe ~600 tokens (the directive plus the chunks). The constraint-encoding moves from the prompt into the tools.

## Failure-mode mapping

How the proposed architecture handles each documented failure mode:

| Failure | v1.3.0 mitigation | Tool-agent equivalent |
|---|---|---|
| `Aliases: ["bounty hunter"]` | Aliases blocklist post-validation | `propose_property("Aliases", ["bounty hunter"], ...)` returns *rejected: 'bounty hunter' resolves to node 456298 (TitleOrPosition); use propose_edge(has_role) instead*. Agent self-corrects. |
| `has_role → Darth Sidious` | Target-type validation in consolidator | `propose_edge(label="has_role", to=Sidious_Character)` returns *rejected: has_role expects TitleOrPosition; Sidious is Character. Did you mean apprentice_of?* |
| `Primary role(s)` on Character | Template-scoped Phase E | `get_template_schema("Character").properties` doesn't list it, so the agent never tries. If it does try, `propose_property("Primary role(s)", ...)` returns *rejected: not a Character property*. |
| `Occupation` AND `has_role` for same fact | Phase G cross-vector dedup | The agent's `resolve_entity("bounty hunter")` succeeds → it goes straight to `propose_edge(has_role)`. It never reaches `propose_property("Occupation")` because the value resolved to a node. |
| `member_of` next to existing `affiliated_with` | Unordered-pair check | `check_existing_edges(from, to)` reveals the existing `affiliated_with`. Agent uses `Annotate` instead of `Add`. |
| `collaborates_with` for non-music | Tightened label description | `find_canonical_label("collaborated with", Character, Character)` returns the Character-context match (not the music-specific one). The synonym table is type-scoped from the start. |
| Novel relationship with no canonical label | Silent drop | `find_canonical_label` returns `null`; agent calls `suggest_new_label` and the suggestion is stored with evidence for review. |

Every failure mode is either *prevented at call time* or *recovered through tool feedback in the same conversation*. The consolidator's sieve layers become structurally unreachable.

## What we keep, what we throw away

### Keep

- `HolocronContextDiscoveryExecutor` — chunk discovery, neighbour fetch, content-hash staleness detection.
- `HolocronChunkBundlingExecutor` — char-budget-aware batching.
- `HolocronApplyExecutor` — two-phase apply with idempotent indexes (Design-018 substrate).
- `kg.enrichments` / `kg.edge_enrichments` schema — the on-disk shape doesn't change; only how proposals reach those collections changes.
- `MongoCheckpointStore` — the workflow framework still drives the executor sequence.
- `HolocronJobService`, the per-job tracker, the activity log, the Frontend dialog UI.
- The continuity firewall, the era-anchor table, the evidence rules — all migrate into the tool-agent's system prompt.

### Throw away

- The structured-output schema (`HolocronAnnotateProposal`, `HolocronFillGapProposal`, `HolocronAddEdgeProposal`, `HolocronNodeProposalPayload`). Replaced by the tool-call return types.
- `HolocronConsolidatorExecutor`'s validation block (~120 lines of `IsAnnotateValid`, `IsFillGapValid`, `IsAddEdgeValid`, Phase E, Phase G, Aliases blocklist). Constraints move into tools; the consolidator becomes a pure cross-batch dedup pass.
- The 200+ lines of system-prompt rules explaining when to use which array, what counts as an edge vs property, what fieldPaths are valid, etc. The tool surface IS those rules.
- The "Canonical edge labels" and "Canonical property fieldPaths" prompt appendices.
- The full structured-output JSON schema, generated by Microsoft.Agents.AI's structured-output binder.

### Add

- A synonym table mapping `(predicate_prose, source_type, target_type) → canonical_label`. Hand-curated to start, generated from real usage patterns over time. Lives in `FieldSemantics` or a new `LabelSynonyms.cs`.
- Six tools (above), implemented as `AITool` instances bound to the agent.
- A new `kg.label_suggestions` collection + admin UI for reviewing vocabulary-growth signal.
- A "is-this-a-proper-noun-name" heuristic for Aliases (regex + length + capitalisation pattern; not LLM-judged).

## Risks

1. **Tool-call latency.** Each chunk-batch becomes N tool calls instead of 1 LLM call. For Anakin's ~50 batches × ~10 facts/batch × ~5 tool-calls/fact, that's 2500 tool calls vs. 50 LLM calls today. Tools are fast (single Mongo lookup each, indexed) — projected ~200–400 ms per call against the local MongoDB Atlas instance, so 8–17 minutes of tool-call latency for an Anakin run. Plus the LLM time, which is comparable to today (the model still reads the same chunks). **Net:** runs probably 1.5–2× slower. Acceptable trade — Anakin's run is already ~30 min; making it 50 min for correct-by-construction output is a good deal.
2. **Synonym table coverage gaps.** A hand-curated synonym table will start with maybe 100 entries. Real prose has thousands of phrasings. If the agent's `find_canonical_label("X")` returns `null` too often, runs will produce mostly `suggest_new_label` calls and few staged proposals. **Mitigation:** seed the table from the existing `FieldSemantics.Relationships` plus all the per-source-type relabelling code in Design-024. After the first 5–10 dev runs, top suggestions tell us where the real coverage gaps are.
3. **Tool-call error loops.** If a tool returns `rejected`, the agent might retry with the same wrong call. **Mitigation:** the rejection message includes a *suggested correction* ("Did you mean apprentice_of?"). Also: cap retries per fact at 3, after which the fact is logged and skipped.
4. **Cross-batch consistency.** Each batch is its own conversation, so the agent in batch 7 doesn't know what batch 5 staged. **Mitigation:** the apply step's existing dedup handles this; cross-batch overlap is already common with the current pipeline and the dedup logic survives.
5. **Microsoft.Agents.AI multi-step tool-using agent ergonomics.** The framework supports `AIAgent` with bound `AITool` instances and looped tool calls. The pattern is well-trodden in the framework — DataExplorerToolkit and GraphRAGToolkit already work this way. **Mitigation:** the Holocron agent is a more constrained case than DataExplorerToolkit (smaller tool set, single domain); migration risk is moderate, not high.
6. **The agent might over-tool.** A clever LLM might `resolve_entity` every noun in every chunk, even when the chunk is filler. **Mitigation:** budget the agent ~N tool calls per chunk (where N is small, e.g. 5) and tell it to skip filler chunks. If overshoot is a real problem, add a `read_chunk(chunk_id) → text` tool and force the agent to explicitly load each chunk it wants to mine, capping per-batch chunk reads.

## Open questions

1. **Should `propose_edge` and `propose_property` write directly to the staging collection, or accumulate in conversation state?** Direct writes simplify resume (the collection IS the state), but make tool-call rejection invisible to the framework checkpoint store. **Lean toward:** accumulate in workflow state per batch, flush to staging at end of batch — same shape as current Design-020.
2. **How rich does the synonym table need to be on day 1?** Could we derive it programmatically from the existing infobox-field → canonical-label mappings in `FieldSemantics`? Many predicates the LLM uses are paraphrases of those field labels.
3. **Aliases value-shape detection.** Is a regex-based heuristic ("looks like a proper noun, no role-words, ≥1 capital, no possessive constructions like *X's apprentice*") sufficient, or do we need a lightweight classifier? Start with regex; revisit if it's brittle.
4. **Tool-call telemetry.** Every tool call is a structured event — should we surface them to the Frontend's per-node activity log so users can see *exactly* what the agent reasoned and got rejected for? Probably yes; cheap to do, very high signal for debugging.
5. **Versioning.** When the synonym table grows or labels change, how do we invalidate enrichments produced under the old table? Same `agentVersion` mechanism as today — bump version, mark stale.
6. **Backward compatibility with Design-020 / Design-021.** The framework checkpoint shape changes (different state DTOs). Resume of in-progress v1.x runs after the cutover: probably not worth supporting — drain queues first, deploy, restart. Existing applied enrichments are unaffected (they live in `kg.enrichments` / `kg.edge_enrichments` regardless of how they got there).

## Migration path

This is a meaningful refactor — ~2 days of work. Stage it:

### Phase A — Synonym table + read tools (1 day)

- Generate `FieldSemantics.Synonyms` from existing `FieldSemantics.Relationships` (each field-label → its canonical label, tagged with source × target types).
- Implement `resolve_entity`, `find_canonical_label`, `check_existing_edges`, `get_template_schema` as `AITool` instances.
- No behaviour change yet — tools exist but aren't used.
- Unit tests: synonym lookup correctness, entity resolution against test fixture.

### Phase B — Tool-using extractor, behind a feature flag (0.5 day)

- New `HolocronToolAgentExtractor` executor implementing the agent loop.
- Wire-in alongside `HolocronProposalExtractorExecutor`; pick by config flag (`Settings.Holocron.UseToolAgent: bool`).
- Run on dev DB only; compare output against current v1.3.0 on the same source pages (Asajj, Anakin, Mace, Padmé).
- Manual triage of differences — which sieves now structurally unreachable, which legitimate signal newly captured.

### Phase C — Write tools + consolidator simplification (0.5 day)

- Implement `propose_edge`, `propose_property`, `suggest_new_label`.
- Strip the corresponding validation from `HolocronConsolidatorExecutor` — it becomes a cross-batch dedup pass only.
- Bump `AgentVersion` to `holocron-v2.0.0`.

### Phase D — Cut over (0.5 day)

- Flip the feature flag default. v1.x extractor becomes legacy.
- Drain in-progress jobs by waiting; do not migrate them.
- Monitor first 10 runs. Triage `kg.label_suggestions` weekly.

After 2–4 weeks of clean dev-DB operation, retire the v1.x extractor entirely.

## Success criteria

A run on Asajj Ventress (page 453169) under v2.0.0 should produce:

- **Zero** Aliases values that resolve to a node of type TitleOrPosition / Government / Organization / Religion / Species / Family / MilitaryUnit.
- **Zero** property values that overlap with the name of any node receiving an edge in the same run.
- **Zero** `addEdges` proposals where target type doesn't match the label's `ExpectedTargetTypes`.
- **Zero** property proposals using a fieldPath not in `ForTemplate("Character").Properties`.
- **Non-zero** `kg.label_suggestions` entries — the agent finding real coverage gaps in the canonical vocabulary.
- A complete tool-call trace per batch in the activity log, showing exactly which facts were extracted and how each was encoded.

The user's three rules become structurally enforced rather than caught after the fact.

## Decision points

Before implementation, we need to commit to:

1. **Tool-using agent as the primary architecture.** Not a side-experiment.
2. **Microsoft.Agents.AI as the framework.** No reverting to raw `IChatClient` plumbing for this — the framework's tool-binding is the load-bearing piece.
3. **The synonym table as the canonical mapping authority.** Future "the LLM should pick" instincts get redirected to "the synonym table should know."
4. **`kg.label_suggestions` as a first-class collection.** Vocabulary growth becomes a real workflow with admin review, not a silent drop.
5. **Discarding v1.x after the cutover.** Two parallel implementations is permanent tech debt; commit to a clean retirement.

If we're aligned on those five, the rest is execution.
