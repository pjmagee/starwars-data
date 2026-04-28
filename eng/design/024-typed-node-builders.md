# 024 — Typed NodeBuilders: per-type infobox extraction strategy

Status: in progress
Date: 2026-04-28
Author: Patrick Magee
Cross-refs: [Design-007 — KG per-type builders](007-kg-per-type-builders.md), [Design-013 — KG property/edge duality](013-kg-property-edge-duality.md), [Design-021 — Edge bound provenance](021-edge-bound-provenance.md), [Design-023 — Character roles as edges](023-character-roles-as-edges.md) (superseded by this doc)

## Problem

The KG ETL pipeline has 45 per-type NodeBuilders under
`src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/Types/`. **44 of them
are 12–16-line stubs that only declare a NodeType.** Only
`TradeRouteNodeBuilder` (35 lines) does any per-type extraction work. Every
node is built by the generic `NodeBuilderBase.Build` loop, which applies a
single global `FieldSemantics` dictionary to every infobox field regardless
of the source node's type.

The infrastructure (Design-007) was set up so that per-type logic *could*
live in subclasses, but the actual logic was never written. Across a
schema-survey of the top 30 node types in `starwars-dev`, this manifests as:

1. **Generic edge labels lose source-type semantics.** A Character's
   `Affiliation(s)` field maps to the same `affiliated_with` label whether
   the target is an Organization (a faction membership), a Religion (a
   member-of-order), a Family (a family-membership), a TitleOrPosition (a
   role assignment), or a Character (an ownership/allegiance). The Holocron
   agent has no way to refine the relationship semantically because the
   source-of-truth says they're all the same kind of thing.
2. **Per-side semantics are silently flattened.** Battle infoboxes have
   `commanders1`/`commanders2`/`commanders3`/`commanders4` for each
   belligerent side. All four emit identical `commanded_by` edges. The
   side-index is lost — answering "who commanded the Separatists at
   Geonosis" requires a join through `belligerent` edges to find which side
   was Separatist, which is fragile and slow.
3. **Type-named field variants fragment a single semantic relationship.**
   Sector infoboxes have ~30 distinct conflict fields ("Galactic Civil War",
   "Clone Wars", "Yuuzhan Vong War", "Great Sith War", …). Each emits its
   own auto-normalised edge label (`galactic_civil_war`, `yuuzhan_vong_war`,
   …). The same logical relationship `has_conflict` is fragmented into 30+
   labels with no way to query "all conflicts in this sector."
4. **Several high-frequency fields are silently kept as raw text.** Year's
   `Chancellor` (11% of pages), TitleOrPosition's `Powers` (7.5%), Sector's
   `Sector capital`/`Subsectors`/`Stations`, ReferenceMagazine's `Featured`
   — all unclassified. They have meaningful Links data; the data is stored,
   but as raw strings on `properties[Label]`, never as edges.
5. **The empty-label `""` row is ISBN.** 92.5% of Books, 98% of
   ReferenceBooks, 22.5% of ComicBooks, and 12.5% of MagazineIssues have
   one. Wookieepedia's template emits ISBN with no label. The current
   pipeline preserves it as `properties[""]`, which is invisible to every
   downstream consumer.
6. **Several `LabelDefinition.Targets` arrays are wrong.** The dictionary
   declares `associated_culture` targets `["CulturalGroup"]`, but in
   practice 95%+ of `Culture` field links resolve to Species or Religion.
   Either the type-filter is dead code or it's silently wrong.

The schema survey (full per-type findings appended below) shows ~10–15
high-leverage per-type opportunities, ranging from one-edit relabelings to
substantial structural fixes (per-side index encoding, ISBN normalisation).

The intent of Design-007 was to enable per-type logic. The next step is to
**actually write it**, holistically and consistently, across all the types
where it pays off.

## Goals

1. **Fill in the per-type `NodeBuilder` overrides** for the ~12 types
   where the survey identified substantive per-type semantics.
2. **Generalise overloaded fields by source-type × target-type.** Specifically:
   `Affiliation` mapped to `member_of` / `member_of_family` / `owned_by` /
   `works_for` / `has_ethnicity` / `has_role` based on the (sourceType,
   targetType) pair.
3. **Preserve per-side index** on conflict-style relationships
   (Battle/Mission/Event/Duel/War/Campaign) so multi-belligerent queries
   are possible.
4. **Unify aliased field names.** Sector's 30+ war-named fields collapse to
   `has_conflict`. Year's titled-leader variants (Chancellor/Head/Chief)
   parallel `Emperor`. TelevisionEpisode's `Timeline (Canon)` /
   `Timeline (Legends)` collapse to `Timeline`.
5. **Normalise empty-label rows.** ISBN becomes a real `ISBN` property.
6. **Fix wrong `LabelDefinition.Targets`** where the survey shows the
   declared target type doesn't match reality.
7. **Document per-type semantics** in the builder source itself — every
   type-specific override carries a doc-comment summarising what it does
   and why, so the next person reading `OrganizationNodeBuilder.cs` doesn't
   have to re-derive the rule from the data.

## Non-goals

- **Wholesale schema redesign.** This is incremental. We're extending the
  per-type override system that already exists, not replacing it.
- **Solving the Holocron prompt-bias issue.** That's a separate concern
  (the agent prefers `affiliated_with` over `apprentice_of` because of how
  it expresses chunks). This design changes what the *base data* looks like;
  Holocron benefits because the candidate edges are now better-typed, but
  prompt tuning is its own follow-up.
- **Building new node types or merging existing ones.** The 126 node types
  are what the corpus has; we extract them better, we don't reorganise them.
- **Source-data ETL pipeline rewrites** (Phase 1 wiki download, raw page
  parsing). This design only touches Phase 5 (`InfoboxGraphService` +
  `NodeBuilders`).
- **Frontend changes.** New edge labels surface through the existing
  `kg.labels` registry and the existing label-chip UI. The graph explorer
  picks up new labels automatically.

## Architecture

### Where the work happens

Each per-type `NodeBuilder` extends `NodeBuilderBase` and overrides one or
more of these existing extension points:

| Hook | When it fires | What it can do |
|---|---|---|
| `OnRelationshipExtracted(ctx, label, primaryLinks, properties)` | Once per relationship field after the generic loop emits its edges | Inspect the parsed link payloads; emit additional properties; doesn't see the emitted edges directly |
| `OnFinalize(ctx, node, edges)` | Once per node after all fields processed and node assembled | Mutate `node` (properties, derived fields) and `edges` (relabel, drop, add) |

For the per-type semantics in this design, **`OnFinalize` is the workhorse**.
By the time it fires, `edges` contains every edge the generic loop emitted,
each carrying `FromId`, `ToId`, `Label`, and the source-context that
identifies which infobox field it came from. The override walks this list
and applies type-specific rules.

### A new piece: target-type lookup

The blocker for per-type override logic is that `OnFinalize` doesn't know
the *type* of the edge target — only its PageId. The survey's most-impactful
findings (Affiliation by target-type, Battle commanders by valid-target-type
filter) all need the target type.

The fix: **add `NodeTypeByPageId` to `NodeBuilderContext`**. The orchestrator
in `InfoboxGraphService.BuildAsync` already has all the data — it builds
`wikiUrlToPageId` from a query that selects `_id, wikiUrl, type`. The same
pass populates a `Dictionary<int, string>` keyed by PageId. Cost: trivial
(memory-bounded by node count, ~166K entries × 16 bytes = 2.6 MB).

```csharp
public sealed record NodeBuilderContext(
    int PageId,
    string Title,
    string Type,
    /* … existing fields … */
    IReadOnlyDictionary<string, int> WikiUrlToPageId,
    IReadOnlyDictionary<int, string> NodeTypeByPageId  // new
);
```

In a `NodeBuilder.OnFinalize`, the override now reads:

```csharp
foreach (var edge in edges)
{
    var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
    // ... apply per-type rules
}
```

### The override patterns

Three patterns recur across the per-type overrides identified in the survey:

**Pattern A — Source × Target relabel.**
The same generic label maps to different semantics depending on
(sourceType, targetType). Example: `affiliated_with` from Character →
TitleOrPosition becomes `has_role`. Implementation: `OnFinalize` walks
edges, switches on target type, mutates `edge.Label` and
`edge.ReverseLabel`.

**Pattern B — Per-index encoding.**
Numbered field variants (`commanders1`, `commanders2`, …) need to preserve
the index. Implementation: `OnFinalize` reads the originating field name
from a context that the generic loop populates (currently lost — see
"Required base-class change" below), and stamps the side index into
`edge.Meta.Order` (already exists) or a new `edge.Meta.SideIndex`.

**Pattern C — Field-alias collapse.**
30+ field names that all mean the same thing (Sector's war fields). Either
a per-type alias map applied at field-resolution time, or a post-pass in
`OnFinalize` that walks edges and remaps known label-aliases.

### Required base-class changes

To unblock Patterns B and C, the base `NodeBuilderBase.Build` needs to
preserve the **originating field name** on each emitted edge. Today the
edge carries `Evidence = $"Infobox field '{label}'"` which is human-readable
text — fine for audit, but parseable only by string-matching. Add a
structured `edge.Meta.SourceFieldLabel` so per-type overrides can branch
on it cleanly.

Also: a `NodeTypeByPageId` lookup as described above.

### Validation rules

Every override produces a small, focused change. To keep the type-builder
files coherent and reviewable:

- Each override is documented with a 3–5 line doc-comment explaining
  *what* it changes, *why* (with a survey-frequency citation: "the survey
  found 116 affiliated_with → TitleOrPosition edges from Character that
  semantically mean has_role"), and the migration path for existing data
  (whether a one-shot SQL/script is needed or whether the next Phase 5
  rebuild handles it).
- New canonical edge labels (anything emitted that isn't already in
  `FieldSemantics.Relationships`) get a one-line entry in the dictionary
  *and* a brief description that's surfaced through `kg.labels`.
- A new edge label that overrides an existing one (e.g. `affiliated_with`
  → `has_role`) goes through the same canonical-vocabulary path; the agent
  prompt picks up the new label automatically.

### Data migration approach

The Holocron pipeline has only ever run on 3-4 test nodes against
`starwars-dev`. Total enrichment row count is ~20. There is **no
production data to preserve**. So the migration story is simple:

1. **Wipe the enrichment collections** before running Phase B's first
   Phase 5 rebuild:
   ```js
   db.getCollection("kg.edge_enrichments").drop();
   db.getCollection("kg.enrichments").drop();
   db.getCollection("kg.events").drop();
   db.getCollection("kg.enrichment_jobs").drop();
   db.getCollection("kg.node_processed_chunks").drop();
   ```
2. **Run Phase 5.** It produces the corrected edge set under the new
   per-type rules.
3. **Re-run Holocron** on whatever nodes you want to retest (Anakin /
   Asajj / etc) — clean slate, fresh enrichments under the new schema.

No lockstep label migrations. No "preserve existing enrichments across
schema changes" rule. The corpus is small enough and the Holocron usage
sparse enough that wipe-and-rebuild is the cleanest path.

Once Holocron is running at production scale (post the first real
release), this calculus changes — at that point an enrichment-label
migration would be needed alongside any further schema changes. Flag for
revisit.

## Implementation phases

This is a multi-step rollout. **Each phase is a separate PR / commit so a
fresh-context agent can execute one at a time.**

### Phase A — Infrastructure (foundation for the rest)

1. Add `NodeTypeByPageId` to `NodeBuilderContext`.
2. Populate it in `InfoboxGraphService.BuildAsync` from the same query
   that builds `wikiUrlToPageId`.
3. Add `Meta.SourceFieldLabel` to `EdgeMeta` (used by per-side index
   patterns). `[BsonIgnoreIfNull]` so older rows don't carry it.
4. Generic `Build` populates `SourceFieldLabel` on every edge it emits.
5. Build + Unit tests.

This phase ships with no behaviour change. It's preparing the ground.

### Phase B — Highest-leverage per-type overrides

In rough priority order (impact × ease):

1. **CharacterNodeBuilder** — Affiliation(s) source × target-type relabel
   (each is a clean replacement, no parallel-emission):
   - `→ TitleOrPosition` becomes `has_role` (116 edges)
   - `→ Family` becomes `member_of_family` (754)
   - `→ Religion` becomes `member_of` (4,128 edges) — religious orders are
     memberships, not affiliations, semantically
   - `→ Species` becomes `has_ethnicity` (205)
   - `→ City` becomes `from_city` (205)
   - `→ Company` becomes `works_for` (1,274)
   - `→ Military_unit` / `→ Fleet` becomes `serves_in` (8,037)
   Plus: pick up Domain (Yuuzhan Vong) and Caste fields, currently
   unclassified, when Links present → emit appropriate edges.
2. **DroidNodeBuilder** — Affiliation:
   - `→ Character` becomes `owned_by` (358)
3. **BattleNodeBuilder + MissionNodeBuilder + DuelNodeBuilder + WarNodeBuilder + CampaignNodeBuilder + EventNodeBuilder** — per-side encoding:
   - `commanders1/2/3/4` → stamp `Meta.SideIndex` 1-4
   - `ppl1/2/3/4` → same
   - `unit1/2/3/4` → same
   - `side1/2/3/4` → same
   - Filter: drop `commanders` edges where target type is in
     `{Religion, CelestialBody, Species, Family, Company}` — those are
     bad data per the survey.
4. **SectorNodeBuilder** — Field-alias collapse:
   - All war-named fields → `has_conflict`
   - All era-named fields → `in_era`
   - "Sector capital" → `has_capital`
   - "Subsectors" → `has_subsector`
   - "Stations" → `has_space_station`
5. **YearNodeBuilder** — Add typed-leader edges:
   - "Chancellor" → `has_chancellor`
   - "Head" → `has_head`
   - "Chief" → `has_chief`
6. **TitleOrPositionNodeBuilder** — Promote unclassified to relationships:
   - "Organization" / "Government" → `position_in` (303 edges/200 sample)
   - "Powers" / "Term length" → recognised Properties

### Phase C — Lower-frequency per-type overrides

7. **CelestialBodyNodeBuilder** — `speaks_language` source-aware: from a
   CelestialBody, the forward label becomes `has_language` (1,649 edges).
8. **TelevisionEpisodeNodeBuilder** —
   - "Guest star(s)" → `featured_actor` (target Person/Character)
   - "Timeline (Canon)" / "Timeline (Legends)" → normalise to `Timeline`
   - "Production company" → `produced_by`
9. **BookNodeBuilder + ReferenceBookNodeBuilder + ComicBookNodeBuilder + MagazineIssueNodeBuilder** —
   Empty-label `""` rows get normalised to `ISBN` Property. ISBN2/ISBN3
   variants similarly. (This is arguably an ETL concern — could move to
   Phase 1 raw extraction — but the same fix can land here as an interim.)
10. **ReferenceMagazineNodeBuilder** — "Featured" → `features` relationship.
11. **IndividualShipNodeBuilder + StarshipClassNodeBuilder** — Affiliation:
    - `IndividualShip → Military_unit / Fleet` becomes `assigned_to`
    - `StarshipClass → Religion / Species` becomes `designed_for`
12. **OrganizationNodeBuilder** — Filter: drop `led_by` edges where target
    type is Organization (bad-data noise).
13. **WeaponNodeBuilder + DeviceNodeBuilder + ArtifactNodeBuilder + LightsaberNodeBuilder** —
    Culture/Socio-cultural group(s) target is overwhelmingly Species or
    Religion, NOT CulturalGroup. Either fix the dictionary's Targets array
    or split into per-target labels.
14. **FoodNodeBuilder** — "Race" field carries Species links → `has_species`.
    "Inedible by" mirrors `Edible by` as `not_edible_by`.

### Phase D — Verification + memory updates

For each phase:
1. Run a Phase 5 rebuild on `starwars-dev`.
2. Spot-check ~5 representative nodes per affected type.
3. Confirm new edge labels appear in `kg.labels`.
4. Confirm Holocron's FillGap candidate list now includes the newly-typed
   edges (they'll be `Lifecycle`-tagged automatically per Design-021).
5. Update `MEMORY.md` with the per-phase ship summary.

A single comprehensive verification pass after Phase B suffices for that
batch; Phase C overrides each get smaller per-type spot-checks.

## Risks

- **`NodeBuilderBase` becomes a hot mod-target.** Adding the
  `SourceFieldLabel` propagation touches the inner generic loop. Risk: any
  bug here affects every type. Mitigation: comprehensive Unit tests on the
  base loop before any per-type override lands.
- **Per-side `SideIndex` encoding doesn't change query patterns
  immediately.** Until consumers (e.g., the graph explorer) start
  reading the side index, Battle queries still don't answer "Separatist
  commanders at Geonosis" — they answer "all commanders". The data is
  there; the consumer has to opt in. Mitigation: add a follow-up frontend
  task to the per-node panel and graph explorer.
- **Some overrides cross continuity (Canon vs Legends).** Edge data may
  vary in shape between continuities. Mitigation: each override checks
  `ctx.Continuity` if behaviour differs; otherwise apply uniformly.

## Open questions

- Should the target-type lookup live on `NodeBuilderContext` (as proposed)
  or on a separate `INodeTypeResolver` service injected into builders?
  Argument for service: easier to mock in tests. Argument for context:
  matches the existing pattern (`WikiUrlToPageId` is on context).
  Default: context, for symmetry. Revisit if test ergonomics suffer.
- How aggressive should we be about *dropping* bad-data edges (e.g. Battle
  commander → Religion)? The survey shows ~10% of commanders edges target
  invalid types. Mitigation could be: drop them entirely, or keep them and
  flag with `meta.flag = "InvalidTargetType"`. Default: drop them. Revisit
  if downstream consumers miss the data.
- Should we add a `NodeBuilderBase.OnEdgeFiltered(edge, reason)` hook so
  per-type drop decisions are visible in the structured logs? Probably
  yes, for future debuggability.

## Verification

Acceptance criteria, deferred per phase but cumulatively:

- [ ] Phase 5 rebuild produces zero edges with `label == ""` (was up to
      98% on ReferenceBook).
- [ ] `kg.edges` contains zero `affiliated_with` edges from Character to
      TitleOrPosition (those become `has_role`).
- [ ] `kg.edges` contains ≥1,000 `has_role` edges (was zero from the
      survey baseline).
- [ ] Battle and Mission edges' `meta.sourceFieldLabel` is populated
      (`commanders1`, `commanders2`, etc.) and the side index is
      preserved in `meta.sideIndex`.
- [ ] Sector's `has_conflict` count is ≥30 × the count of any single
      war-named label that previously fragmented it.
- [ ] Year's `has_chancellor` edge count rises from 0 to ≥hundreds.
- [ ] Holocron's FillGap candidate list (during a representative run)
      now includes the newly-typed edges.
- [ ] Each updated `*NodeBuilder.cs` carries a doc-comment summarising
      its per-type rules.
- [ ] Unit tests cover at least one representative edge case per
      override (relabel, drop, alias-collapse).

## Cost estimate

Rough order-of-magnitude per phase:

- **Phase A (infrastructure):** ~1 day (1 file touch in
  `NodeBuilderContext`, 1 in `NodeBuilderBase`, 1 in `InfoboxGraphService`,
  unit tests).
- **Phase B (high-leverage):** ~3 days. Six builders, mostly relabel-
  pattern overrides. Migration script for existing enrichment label mismatches.
- **Phase C (lower-frequency):** ~2 days. Eight more builders, mostly
  one-off rules per type.
- **Phase D (verification):** ~1 day after each batch ships.
- **Holocron prompt tune (post-release):** ~half day. The agent's prompt
  references the canonical labels; new labels show up automatically but
  may benefit from a small "use has_role over affiliated_with when role
  context is in chunks" nudge.

Total ~7-8 days of focused work, in distinct phases. Each phase ships
independently — if Phase B reveals issues, Phase C can wait.

---

## Appendix A: Schema-survey raw findings (top 30 types)

Key per-type observations from the corpus survey (200-doc sample per
type). Format: `Field (frequency/200) → Classification`. P=Property,
R=Relationship, T=Temporal, **U**=Unclassified silently kept as raw text.
**Bold** = high-leverage opportunity.

(See full survey output kept in conversation history for verbatim
findings; only headlines preserved here.)

- **Character** (43,732 nodes): Affiliation(s) is the most overloaded
  field. Per-target-type breakdown: 19,827 → Government, 12,567 →
  Organization, 7,499 → Military_unit, 4,128 → Religion, 1,274 →
  Company, 887 → Character, 801 → Structure, 754 → Family, 538 →
  Fleet, 205 → Species, 205 → City, **116 → TitleOrPosition**.
- **System** (11,600): clean. No unclassified fields above 5%.
- **Person** (10,349): clean.
- **CelestialBody** (8,628): `speaks_language` source-aware re-label
  is the only major change.
- **Species** (6,431): clean.
- **Battle** (4,185): per-side encoding for commanders1-4, ppl1-4,
  unit1-4, side1-4. Bad-target filter for commanders → Religion /
  CelestialBody / Species / Family.
- **TitleOrPosition** (1,504): "Organization" / "Government" / "Powers"
  / "Term length" all unclassified or under-classified.
- **Sector** (1,295): 30+ war-named fields fragment `has_conflict`.
  ~10 era-named fields fragment `in_era`. "Sector capital",
  "Subsectors", "Stations" all carry links and are silently raw text.
- **Book / ReferenceBook / ComicBook / MagazineIssue** (1,214 +
  875 + 2,106 + 1,092): empty-label `""` row is ISBN. 92.5% / 98% /
  22.5% / 12.5% of pages.
- **TelevisionEpisode** (1,108): "Guest star(s)" 12.5% silently raw,
  "Production company" 2%, "Timeline (Canon)" / "Timeline (Legends)"
  21 each (continuity-suffix).
- **Year** (1,072): "Chancellor" 11%, "Head" 4%, "Chief" 3% — all
  silently raw, all carry Character links.

The full survey enumeration (per-field classification across 30 types)
fits in the original survey output and is intentionally not duplicated
here to keep the design doc readable.

---

## Appendix B: Instructions for a fresh-context agent

The next agent picking this up has a clean conversation window. It needs
to be self-sufficient. Read this section as a kickoff brief.

### Where you are in the project

You're working on the StarWarsData KG ETL pipeline. The KG is in
MongoDB (`starwars-dev`), built from Wookieepedia infobox data. Phase 5
rebuilds `kg.nodes` + `kg.edges` from `raw.pages` via a generic
`NodeBuilderBase` loop with type-specific subclasses under
`src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/Types/`.

Most of those subclasses are stubs. This design lays out a phased plan
for filling them in based on a corpus schema survey.

### What you need to know up front

1. Read **all** of [Design-024](024-typed-node-builders.md) (this doc)
   end-to-end before writing any code. Don't skim — the architecture
   patterns and risk-section caveats matter.
2. Read [`NodeBuilderBase.cs`](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs)
   — the inner `Build` method — and confirm where extension hooks fire
   in the order of operations.
3. Read [`FieldSemantics.cs`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs) — the canonical edge-label registry. New labels you
   emit must be registered there; the kg.labels collection is rebuilt
   from this dictionary.
4. Read [`InfoboxGraphService.BuildAsync`](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) end-to-end. Especially the global filter at lines 200-225
   that drops parser-noise edges. Your overrides must run *before* that
   filter, since they may emit relabel-form edges that would otherwise
   be dropped.
5. Familiarise with the Mongo MCP tools — `mcp__MongoDB__find`,
   `aggregate`, `count`. The connection string is in
   `MDB_MCP_CONNECTION_STRING` env var; database name is `starwars-dev`.
   Don't write to production (`starwars`).

### Pre-flight verification (before any code)

Before changing code, validate the survey findings on the live data:

1. Run the Top-30-types aggregation and confirm the type list above is
   still accurate (Wookieepedia adds nodes regularly, but the top 30
   should be stable).
2. Pick 2-3 specific opportunities from Phase B and re-confirm the per-
   target-type breakdown by running the relevant aggregation. For example,
   for `Affiliation: Character → TitleOrPosition`, run:
   ```js
   db.getCollection("kg.edges").aggregate([
     { $match: { fromType: "Character", toType: "TitleOrPosition", label: "affiliated_with" } },
     { $count: "n" }
   ])
   ```
   Expected ~116. If the actual count differs by >10×, the corpus has
   shifted significantly and the design's priorities should be re-derived
   from a fresh survey.
3. Confirm `Affiliation(s)` field is still the dominant overloaded path
   — sample 50 random Character pages and tally how many have
   `Affiliation(s)`. Expected: ~all of them.

### Workflow

For each Phase B / C item:

1. **One PR per phase** (or per builder if a phase touches several
   builders). Don't bundle Phase B + Phase C into one giant PR; the
   reviewer will lose track.
2. **Write the override + the doc-comment + the Unit test all in the
   same PR.** Each override has its own justification; that justification
   should live in the source.
3. **Run a Phase 5 rebuild on starwars-dev after each PR.** Verify the
   acceptance criteria for that override (counts shifted as expected,
   new labels appear in `kg.labels`, no regressions in adjacent types).
4. **Run the Holocron Asajj Ventress smoke test** (~3 min, ~$0.50)
   after Phase B completes — verify the pipeline still works end-to-end
   and FillGap candidate lists include the newly-typed edges.

### Things to be careful of

- **Don't change the `IsPersonRelationshipLabel` filter** at
  `InfoboxGraphService.cs:211`. It's correctly removing parser noise
  (e.g. `apprentice_of → "Jedi Knight"` from misparsed qualifiers). The
  per-type overrides run *before* this filter, so their relabeled output
  bypasses it cleanly.
- **Don't store unclassified field data on `properties[Label]` if the
  field has Links.** Either classify it as a Relationship (preferred)
  or filter it out. The current behaviour of "silently raw text on a
  property with no consumer" is the bug we're fixing.
- **When adding a new edge label to `FieldSemantics.Relationships`,
  always add the reverse label too.** The reverse is what powers
  inverse-direction queries; missing it makes graphs uni-directional.
- **Wipe Holocron's enrichment collections at the start of Phase B.**
  `kg.edge_enrichments`, `kg.enrichments`, `kg.events`,
  `kg.enrichment_jobs`, `kg.node_processed_chunks` — drop all of them
  on `starwars-dev`. The enrichments came from 3-4 test runs, total
  row count is ~20. Cleaner to wipe and regenerate under the new
  schema than to migrate label-by-label. After Phase 5 rebuild, re-run
  Holocron on whatever test nodes you want fresh data for (~3 min for
  Asajj). This rule revisits when Holocron is in production.
- **All relabels are clean replacements.** No parallel-emission. If
  `Affiliation(s) → Religion` becomes `member_of`, the edge gets the
  new label only — `affiliated_with` is gone for that triple. The
  semantic categories on `LabelDefinition` (e.g. `category: "membership"`)
  drive UI grouping; there's no need to keep the old label as a
  fallback signal.

### When you finish

- Update `MEMORY.md` with a project memory referencing the new
  per-type builder. Include: phase number, what changed, edge-count
  delta seen on starwars-dev, ship date.
- If something in the survey turned out wrong (e.g. the Character→
  TitleOrPosition count was 1,160 instead of 116), update the
  appendix in this design doc.
- Run the full Unit tier (`dotnet test --project src/StarWarsData.Tests
  --filter "TestCategory=Unit"`) and confirm 67/67 pass before merging.

### When to stop and ask the user

- If a per-type override would change >5,000 edges in a single PR.
  Confirm the magnitude is intended.
- If the per-type rule conflicts with an existing FieldSemantics entry
  (i.e., the dictionary says `affiliated_with` targets X, but the
  override says it should target Y). The dictionary's target list may
  be wrong — but confirming with the user is cheaper than a failing
  Phase 5 rebuild.
- If a migration would touch production (`starwars`). Migrations only
  run on `starwars-dev` from your hands; production is the user's call.

---

## References

- [Design-007 — KG per-type builders](007-kg-per-type-builders.md) —
  set up the per-type infrastructure that this design now uses.
- [Design-013 — KG property/edge duality](013-kg-property-edge-duality.md)
  — the framework for deciding what's a property vs an edge.
- [Design-021 — Edge bound provenance](021-edge-bound-provenance.md) —
  the temporal-bounds story; new edges from this design inherit
  Lifecycle bounds and are refinable by Holocron.
- [Design-023 — Character roles as edges](023-character-roles-as-edges.md)
  — superseded by this doc (Character has_role is one phase of this
  broader plan).
- [`NodeBuilderBase.cs`](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs)
  — the generic loop being extended.
- [`FieldSemantics.cs`](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs)
  — canonical-label registry.
- [`InfoboxGraphService.cs`](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs)
  — the orchestrator that runs builders + applies global filters.
