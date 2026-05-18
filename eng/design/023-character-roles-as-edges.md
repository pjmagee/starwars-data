# 023 — Character roles as `has_role` edges to TitleOrPosition nodes

Status: Superseded by [Design-024 — Typed node builders](024-typed-node-builders.md). The outcome shipped (2026-04-28, `ed5d53f246`): `has_role`/`held_by` registered in `FieldSemantics` and the Character→TitleOrPosition promotion implemented — but via Design-024's `CharacterNodeBuilder.OnFinalize` per-type override, **not** the `InfoboxGraphService.cs:211` drop-filter flip proposed here (that filter was deliberately left intact). The standalone `0017-titles-to-has-role-edges.js` migration was never created; the typed-builder rebuild handles it. Treat Design-024 as authoritative.
Date: 2026-04-28
Author: Patrick Magee
Cross-refs: [Design-001 — Temporal facets](001-temporal-facets.md), [Design-035 — KG per-type builders](035-kg-per-type-builders.md), [Design-018 — KG enrichments architecture](018-kg-enrichments-architecture.md), [Design-021 — Edge bound provenance](021-edge-bound-provenance.md)

## Problem

The Wookieepedia `Character` infobox has one heterogeneous `Titles` field:

```text
Asajj Ventress (Wookieepedia infobox)
  Titles: [
    "Asajj Ventress",                  # her own name
    "Commander Asajj Ventress",        # name + rank prefix
    "Darth Tyranus's assassin",        # role tied to a master
    "Sith apprentice",                 # role
    "Bounty hunter",                   # profession
    "Nightsister",                     # role + faction
    ...
  ]
```

Phase 1 ETL extracts this verbatim into `kg.nodes.properties.Titles` as a string array. Three problems flow from this:

1. **Names and roles are conflated.** "Asajj Ventress" and "Sith apprentice" are different kinds of facts about her — the first is an alias, the second is a temporal occupation.
2. **Roles are not temporal.** As an array of strings, there's no place to record *when* she was a Sith apprentice vs *when* she was a Bounty hunter — she was both at different times.
3. **Holocron enrichment funnels role-like proposals into `Titles`.** The agent has no other "list of strings" bucket that fits role context, so it Augments `Titles` with values like `"Sith apprentice"` and `"Padawan of Ky Narec"` — exactly mixing the two kinds of facts the schema couldn't distinguish in the first place.

### The infrastructure already exists

`kg.nodes` contains **1,504 `TitleOrPosition` nodes** representing exactly the things `Titles` array entries resolve to: `Bounty hunter`, `Senator`, `Sith Lord`, `Padawan`, `Jedi Knight`, `Jedi Master`, `Galactic Emperor`, `Sith apprentice`, etc. Each of these is a real Wookieepedia article with its own page.

### The data is being thrown away

`kg.edges` has **974 edges to `TitleOrPosition` targets** under labels like `has_position`, `affiliated_with`, `governed_by`. But **zero are character→role edges**, because of this filter at
[InfoboxGraphService.cs:211](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L211):

```csharp
// Drop qualifier edges: TitleOrPosition targets on person-relationship labels
if (targetType == KgNodeTypes.TitleOrPosition && IsPersonRelationshipLabel(edge.Label))
{
    droppedQualifier++;
    continue;
}
```

The intent was to drop parser confusions like
`Anakin --[apprentice_of]--> "Jedi Knight"`
where the parser failed to distinguish a person-relationship target from a qualifier. The implementation threw out the baby with the bathwater: every potential character→role edge is dropped at every Phase 5 run, leaving the entire role data plane empty.

### Concrete failure case

The Holocron run on Asajj Ventress (jobId `69f0dee12c62cee6d82dc3e5`) produced a NodeProposal:

```json
{
  "fieldPath": "Titles",
  "values": ["Sith apprentice", "Bounty hunter", "Nightsister", "Dathomirian Nightsister",
             "warrior", "Mercenary", "Commander Asajj Ventress",
             "Darth Tyranus's assassin", "Sith Acolyte"],
  "claim": "Asajj Ventress held these additional titles/roles."
}
```

This is the LLM doing exactly the right thing given the schema it sees: there's nowhere else to put role information. But the result is wrong on multiple axes:

- **No temporal bounds.** When was she a Bounty Hunter vs a Sith Apprentice? Lost.
- **Mixes a name** ("Commander Asajj Ventress") with **roles** ("Sith Apprentice").
- **Frontend display** (per the user's screenshot) shows "Augment → Titles: Sith apprentice, Bounty hunter, Nightsister, Dathomirian Nightsister, warrior, Mercenary, ..." which makes the Titles concept incoherent.

## Goals

1. **Promote roles to first-class temporal edges** — `Character --[has_role]--> TitleOrPosition`, with `fromYear` / `toYear` and `meta.boundsSource` (Design-021).
2. **Restore `Titles` to its actual semantic meaning** — names, aliases, and identifying nicknames only.
3. **Stop dropping the data** — Phase 5 currently filters out every character→role edge; un-drop them under the new label.
4. **Migrate existing data** — parse existing `properties.Titles` arrays, emit `has_role` edges for entries matching `TitleOrPosition` nodes, leave non-matches in `Titles` as names/aliases.
5. **Update the Holocron agent** — propose role refinements as `has_role` edges (which Design-021's FillGap already handles), not as `Titles` augments.

## Non-goals

- **Adding a `Roles` property field as a list of strings.** That was the first instinct and it would replicate the same temporal-blindness as the current `Titles` field. The whole point of the redesign is that roles are temporal-by-nature, so they belong on edges.
- **Eliminating `properties.Titles` entirely.** Names and aliases are genuine flat-string facts ("Sheev Palpatine", "Darth Sidious", "The Emperor", "Commander Asajj Ventress"). They stay as a property — just stricter.
- **Building UI for the role timeline view.** The graph explorer's existing year slider already filters edges by temporal bounds; once `has_role` edges land, role visualization comes for free. A dedicated "role timeline" UI is a follow-up if needed, not part of this design.
- **Solving the Holocron `affiliated_with` vs `apprentice_of` label-bias problem** — that's a separate prompt-tuning issue and unrelated to whether roles are edges or properties.

## Architecture

### New canonical edge label

Add to [`FieldSemantics.Relationships`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs#L193):

```csharp
// In the Relationships dictionary, mapped from the "Titles" infobox field label
// when the entry resolves to a TitleOrPosition node:
["Titles"] = new("has_role", "held_by", "Role, position, or office held", ["TitleOrPosition"], "role"),
```

**Important:** `Titles` currently lives in the `Properties` hashset (at line 26). The schema split is:

- An entry in the `Titles` array that **resolves to a `TitleOrPosition` node** → emit a `has_role` edge.
- An entry that **doesn't resolve** (a name like "Commander Asajj Ventress" or "Darth Tyranus's assassin") → keep in `properties.Titles` as a name/alias.

This is the split the user actually wants: `Titles` becomes coherent as "names and aliases," `has_role` carries the structured role information.

### Phase 5 filter change

In [`InfoboxGraphService.cs:210-215`](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L210-L215), the filter changes from "drop all character→TitleOrPosition edges with person-labels" to:

```csharp
// Promote person-relationship labels targeting TitleOrPosition into has_role.
// These were previously dropped; now we recognize them as the role assignments
// they actually are. Person-relationship labels (apprentice_of, master_of, etc.)
// don't make sense pointing at an abstract title node — but the data IS a role
// assignment, just with the wrong label. Re-label to has_role.
if (targetType == KgNodeTypes.TitleOrPosition && IsPersonRelationshipLabel(edge.Label))
{
    edge.Label = "has_role";
    edge.ReverseLabel = "held_by";
    promotedRoleCount++;
    // Do NOT continue — let the edge proceed into filteredEdges.
}
```

The same applies to `ForcePower` / `LightsaberForm` targets at line 217-222 — but those are out of scope for this design (they need their own per-target-type promotion: `wields_power`, `practices_form`, etc.). That's a follow-up.

### Temporal bounds on `has_role` edges

`has_role` edges follow the standard Design-021 provenance model:

| Source of `fromYear`/`toYear` | `BoundsSource` | Refinable by Holocron? |
|---|---|---|
| Infobox value carried explicit years (e.g. `"Sith apprentice (22 BBY – 19 BBY)"`) | `Infobox` | No — hard |
| Phase 5 lifecycle fallback (intersection of character lifespan + the role's own start/end if any) | `Lifecycle` | Yes — refinable |
| No bounds available (target role has no lifecycle, character lifecycle didn't fire) | unset (Unknown) | Treat as hard until tagged |

The Holocron agent, with the v1.1.0 prompt that already handles `[lifecycle, refinable]` candidates, will pick these up automatically once the edges exist. No agent-side code change required — just the candidate list naturally includes more refinable edges.

### Migration of existing `properties.Titles`

This is the chunky part. Existing `kg.nodes.properties.Titles` arrays need to be split:

1. **Build a lookup** of all `TitleOrPosition` node names + their PageIds (~1,504 entries, single Mongo query).
2. **For each character node** with a `Titles` array:
   - For each string in the array:
     - Normalize: trim, lowercase for matching, keep original casing for display.
     - Match against the TitleOrPosition lookup. Try exact match first, then case-insensitive, then a small set of common variants (singular/plural, "Padawan" vs "Jedi Padawan").
     - **Match found** → emit a `has_role` edge `(characterId) --[has_role, fromYear=null, toYear=null, meta.boundsSource=Lifecycle]--> (titleNodeId)`. Apply lifecycle-fallback for bounds.
     - **No match** → keep the string in the new `Titles` property (will be filtered to names/aliases only).
3. **Update `kg.nodes.properties.Titles`** to contain only the unmatched entries (names/aliases).

Migration script: `0017-titles-to-has-role-edges.js`. Idempotent: re-runs with no changes are no-ops because matching entries are already gone from `properties.Titles`.

The match heuristic will not be perfect. Edge cases worth flagging in code:

- **"Commander Asajj Ventress"** — name with a rank prefix. Matches no TitleOrPosition. Stays in Titles. Correct.
- **"Padawan of Ky Narec"** — role with a master qualifier. The plain-text doesn't match any TitleOrPosition node verbatim, but "Padawan" does match. Strategy: first try the full string (no match), then strip qualifier patterns (` of `, ` to `, `'s `) and re-match the prefix. If `"Padawan"` matches → emit `has_role → Padawan`, keep the qualifier in the edge's `meta.qualifier` field.
- **"Dathomirian Nightsister"** — qualifier prefix on a role. Same strategy: try full, then strip prefix words and try again. `"Nightsister"` matches → emit edge with qualifier `"Dathomirian"`.
- **"Sith Lord"** vs **"Sith lord"** — case-insensitive match catches both. Stamp the `_id` from the canonical node.

### Phase 1 / Phase 5 going forward

After migration, every Phase 5 re-run produces `has_role` edges naturally because the filter has flipped from "drop" to "relabel." No special handling for re-runs.

The Phase 5 lifecycle-fallback (Design-021) populates `has_role.fromYear` from `max(character.startYear, role.startYear)` and `has_role.toYear` from `min(character.endYear, role.endYear)`. For most roles, the role node has no temporal lifecycle (e.g. "Bounty hunter" exists across all Star Wars history), so the bound becomes the character's lifespan. That's the soft Lifecycle bound the agent can refine.

### Holocron agent

The agent's prompt (v1.1.0) and validation logic (Design-021) already handle FillGap on Lifecycle-tagged edges. Once `has_role` edges exist with Lifecycle bounds, they appear in the FillGap candidate list automatically. The agent will refine them based on chunk evidence the same way it refined `affiliated_with` edges in the Asajj run.

One small change: add a paragraph to the system prompt steering role refinements specifically:

> When a chunk describes a character holding a specific role at a specific time (e.g. "Asajj became Dooku's apprentice in 32 BBY," "Anakin was promoted to Jedi General during the Clone Wars"), prefer the `has_role` edge over `affiliated_with`. `has_role` directly captures role tenure; `affiliated_with` captures organizational membership. Both can be enriched, but for role-specific timing, FillGap on the `has_role` edge.

### Read side — graph explorer

`has_role` edges flow through the existing `QueryGraphAsync` path. The graph explorer's year slider (`d3-graph.js:filterByYear`) already filters edges by `[fromYear, toYear]` overlap with the slider value — so dragging the slider to "5 BBY" will hide a character's `has_role → Bounty Hunter` edge if its bounds are `[3 BBY, 0 BBY]` and show their `has_role → Sith Apprentice` edge if its bounds are `[19 BBY, 4 ABY]`.

No frontend change required for the slider behaviour. A small UX addition: filter the labels chip-row on the per-node detail panel to expose `has_role` separately so users can toggle it. That's polish, not blocking.

## Implementation phases

**Phase A — Schema + canonical label.**

1. Add `has_role` / `held_by` to `FieldSemantics.Relationships` keyed by `"Titles"`.
2. Update `kg.labels` registry refresh to include the new label (it picks up automatically from `Relationships`).
3. Build + Unit tests pass.

**Phase B — Phase 5 filter change.**

1. Flip the drop-filter at `InfoboxGraphService.cs:211` to a relabel.
2. Trigger a Phase 5 rebuild on `starwars-dev`. Verify `kg.edges` now has non-zero `has_role` edges originating from character nodes.
3. Spot-check Anakin (PageId 452390) and Asajj (453169) — should have multiple `has_role` edges to TitleOrPosition nodes.

**Phase C — Migration 0017.**

1. Write the migration: walk `kg.nodes.properties.Titles`, split each entry into TitleOrPosition matches vs unmatched name strings, emit `has_role` edges + retain only names in `Titles`.
2. Run on `starwars-dev`. Counts: should add ~thousands of `has_role` edges and shrink most `properties.Titles` arrays to 1-3 names each.
3. Idempotency: re-run produces zero further changes.

**Phase D — Holocron prompt update.**

1. Add the role-refinement preference paragraph to `BuildSystemPrompt`.
2. Bump `AgentVersion` → `holocron-v1.2.0`.
3. Rebuild API.
4. Verify: re-run Enhance on Asajj Ventress (cheap, ~3 min). Expect FillGap proposals on `has_role` edges (e.g. `has_role → Sith apprentice [22 BBY, 19 BBY]`).

**Phase E — Verification.**

1. Graph explorer year slider on Asajj at year -32 BBY: she should appear connected to "Bounty hunter" and "Padawan" but not "Sith Apprentice" (which started ~32 BBY, not before).
2. Spot-check that the per-node panel renders role edges separately from other edge labels.

## Risks

- **Match heuristic miscategorises edge cases.** "Darth Tyranus's assassin" is a role-like phrase but the only matchable token is "assassin" (a TitleOrPosition node may or may not exist). False-negative is recoverable: it stays in Titles and Holocron can still refine via `affiliated_with` later. False-positive: a name string could partial-match a role node and get spuriously linked. Mitigation: require the full Titles entry to match (with stripped qualifier patterns), not substring match.

- **Existing `kg.labels` "category" UI may not display `role` consistently.** Phase A adds `category: "role"` — if the frontend filters labels by category and doesn't know about it, the new edges won't appear in role-specific filters. Verify the labels chip-row picks it up.

- **Phase 5 rebuild changes edge counts.** Adding ~thousands of `has_role` edges may shift performance characteristics on graph traversals (more edges per node). The `Limit(halfLimit)` in `HolocronContextDiscoveryExecutor` may now hit the cap on high-role characters. Mitigation: revisit the limit if needed; for most characters the limit is comfortably above what they actually have.

- **Holocron agent confusion during transition.** Until the migration runs, the agent sees the old schema. After Phase B + C, characters' candidate lists include both `has_role` (new) and `affiliated_with → Org` (existing). The v1.1.0 prompt has the era table and lifecycle guidance; v1.2.0 adds the role preference. Agent behaviour should improve, not regress. Worst case: agent over-uses `has_role` and skips `affiliated_with` enrichments — recoverable with a prompt rebalance.

## Open questions

- Should `properties.Titles` be renamed to `Names` or `Aliases` to make the semantic shift visible? Argument for: cleaner mental model, stops accumulating cruft. Argument against: every consumer (frontend, search, AI agent prompts) has hardcoded "Titles." For now, keep the name; revisit after the schema settles.
- Should we extend the same treatment to `ForcePower` and `LightsaberForm` (`wields_power`, `practices_form`)? Probably yes, but separate design — the same pattern applies but the field-source mapping is different.
- Does `has_role` need a per-edge `category` (e.g. "military rank" vs "occupation" vs "honorific")? Maybe later. Initial release ties role category to the target TitleOrPosition node's own properties, not to the edge.

## Verification

Acceptance criteria:

- [ ] `has_role` and `held_by` exist in `FieldSemantics.Relationships` and `kg.labels`.
- [ ] After Phase 5 re-run, `kg.edges` contains at least 5,000 `has_role` edges originating from `Character` nodes.
- [ ] Anakin Skywalker (452390) has `has_role` edges to "Jedi Knight", "Jedi General", "Sith Lord", "Galactic Emperor's apprentice" (or however the wiki lists his roles), with `meta.boundsSource: Lifecycle`.
- [ ] Asajj Ventress (453169) `properties.Titles` shrinks to just names/aliases (`"Asajj Ventress"`, `"Commander Asajj Ventress"`, `"Darth Tyranus's assassin"`); the role-like entries become `has_role` edges.
- [ ] Holocron v1.2.0 run on Asajj produces FillGap proposals on `has_role` edges, not on `Titles` property.
- [ ] Graph explorer year slider hides/shows `has_role` edges based on their temporal bounds.
- [ ] Migration 0017 is idempotent — second run produces zero further changes.

## References

- [Design-021 — Edge bound provenance](021-edge-bound-provenance.md) — `has_role` inherits the BoundsSource provenance model.
- [Design-035 — KG per-type builders](035-kg-per-type-builders.md) — character node builder is where `Titles` extraction logic lives.
- [InfoboxGraphService.cs:200-225](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L200-L225) — the filter being relabeled.
- [feedback memory `holocron_duplicated_validation`](../../) — same pattern: validation logic duplicated; must update both copies.
