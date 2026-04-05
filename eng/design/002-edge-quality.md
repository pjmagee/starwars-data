# Design: Knowledge Graph Edge Quality — Noise Reduction & Semantic Relationships

**Status:** Draft
**Date:** 2026-04-03
**Author:** Patrick Magee + Claude

## Problem

The KG edge extraction in `InfoboxGraphService` creates an edge for every wiki link found in an infobox field. This produces ~694k edges, but roughly **24% are noise**:

- **~38,000 edges point to Year entities** (e.g. `died → 4 ABY`, `date_founded → 19 BBY`). These are temporal metadata embedded in relationship fields, not real semantic relationships.
- **~125,000 edges are unresolved** (toId=0, target page not in the wiki crawl). These are links to non-existent or uncrawled pages.
- **~3,500 edges point to TitleOrPosition entities as qualifiers** (e.g. `apprentice_of → Jedi Master`, `led_by → Grand Moff`). These are role descriptors, not relationship targets.

### Root Cause: Flat Link Extraction

Wookieepedia infobox fields contain **structured text with embedded links**. The Values array has the semantic meaning, while the Links array is a flat list of every hyperlink in the field.

**Example: Galactic Empire → Head of state**

```json
Values: [
  "Galactic Emperor (19 BBY–4 ABY)",
  "Grand Vizier (5 ABY, officially)",
  "Counselor to the Empire (5 ABY, de facto)"
]
Links: [
  Galactic Emperor, 19 BBY, 4 ABY,        ← title + temporal bounds
  Grand Vizier, 5 ABY,                      ← title + temporal bound
  Counselor to the Empire                   ← title
]
```

The ETL creates 6 `head_of_state` edges — 3 TitleOrPosition targets + 3 Year targets. But the actual semantic relationships are "the title 'Galactic Emperor' served as head of state from 19 BBY to 4 ABY". The people who held the title (Palpatine, Mas Amedda) aren't even linked.

**Example: Anakin Skywalker → Masters**

```json
Values: [
  "Qui-Gon Jinn (informal Jedi Master)",
  "Obi-Wan Kenobi (Jedi Master)",
  "Darth Sidious (Sith Master)",
  "Yoda (Force spirit teacher)"
]
Links: [
  Qui-Gon Jinn, Jedi Master,              ← person + qualifier
  Obi-Wan Kenobi, Jedi,                    ← person + qualifier
  Darth Sidious, Sith Master,              ← person + qualifier
  Yoda, Force spirit                        ← person + qualifier
]
```

The ETL creates 8 `apprentice_of` edges — 4 real people + 4 qualifier noise (Jedi Master, Jedi, Sith Master, Force spirit).

## Noise Breakdown by Target Entity Type

| Target Type | Edge Count | Nature |
|---|---|---|
| Year | 37,998 | Temporal metadata — should be edge temporal bounds, not separate edges |
| Era | 902 | Temporal context — same issue |
| TitleOrPosition (as qualifier) | ~3,500 | Role descriptors embedded in relationship fields |
| Unresolved (toId=0) | 125,484 | Links to uncrawled/non-existent pages |
| **Total noise** | **~167,884** | **24% of 694k total edges** |

### Top Noisy Edge Labels

| Label → Target Type | Count | What It Should Be |
|---|---|---|
| died → Year | 10,237 | Already captured as temporal facet (lifespan.end). Edge is redundant. |
| in_timeline → Year | 7,797 | Publication metadata. Should be edge to the actual media, not the year. |
| occurred_at → Year | 7,320 | Battle/Event date. Already captured as temporal facet (conflict.point). Edge is redundant. |
| destroyed → Year | 2,194 | Already captured as temporal facet (construction.end). Redundant. |
| born → Year | 1,972 | Already captured as temporal facet (lifespan.start). Redundant. |
| commanding_officer → TitleOrPosition | 1,195 | Should point to the Character, not the rank title |
| led_by → TitleOrPosition | 738 | Should point to the Character, not the rank title |
| date_founded → Year | 1,090 | Already a temporal facet. Redundant. |

## Proposed Solutions

### Phase 1: Filter Noise at Edge Creation (ETL fix)

In `InfoboxGraphService.BuildGraphAsync`, after resolving a link target to a PageId, check the target entity type before creating the edge:

**Filter rules:**

1. **Skip Year/Era targets** for relationship fields — if the edge label is a structural relationship (head_of_state, apprentice_of, affiliated_with, etc.), don't create an edge to a Year or Era entity. The temporal data is already captured in temporal facets.
2. **Skip unresolved targets (toId=0)** — currently creates ~125k orphan edges. These should be dropped.
3. **Skip qualifier targets** — if the target is a TitleOrPosition or ForcePower entity AND the edge label implies a person-to-person relationship (apprentice_of, master_of, led_by, commanded_by), skip it. The qualifier describes the relationship, not the target.

**Implementation approach:**
The ETL already has the `wikiUrlToPageId` lookup. After resolving the target, look up the target's entity type from kg.nodes (or a pre-built type map) and apply filter rules.

This requires a two-pass approach:

1. First pass: build nodes (already done)
2. Second pass: build edges, filtering by target node type

Or: build a `pageIdToType` map during node construction, then consult it during edge creation.

### Phase 2: Parse Values for Primary vs Qualifier Links

The Values array contains structured text like `"Qui-Gon Jinn (informal Jedi Master)"`. The primary entity is the text BEFORE the parenthetical. The qualifier is inside the parentheses.

For each infobox field value:

1. Parse the primary entity name (before `(`)
2. Match it to a link in the Links array
3. Create an edge only for the primary entity link
4. Store the qualifier text as edge metadata (e.g. `qualifier: "informal Jedi Master"`)
5. Parse temporal bounds from the qualifier if present (e.g. `"19 BBY–4 ABY"`)

This is more complex but produces much higher quality edges with metadata.

### Phase 3: Temporal Edge Enrichment

When a Value contains temporal bounds (e.g. `"Imperial Senate (19 BBY–1 BBY, disbanded by Emperor Sheev Palpatine)"`):

1. Create the primary edge: `has_legislative_branch → Imperial Senate`
2. Set `fromYear = -19`, `toYear = -1` on the edge (temporal bounds)
3. Store the qualifier text: `"disbanded by Emperor Sheev Palpatine"`

This finally populates the `fromYear`/`toYear` fields on `RelationshipEdge` that have been empty since the model was created.

## Impact Assessment

| Metric | Current | After Phase 1 | After Phase 2+3 |
|---|---|---|---|
| Total edges | 694,746 | ~527,000 (drop ~167k noise) | ~500,000 (further qualifier dedup) |
| Edges → Year | 37,998 | 0 | 0 |
| Edges → unresolved | 125,484 | 0 | 0 |
| Edges with temporal bounds | 0 | 0 | ~20,000+ (parsed from Values) |
| Edge noise rate | ~24% | ~0% | ~0% |

## Open Questions

1. **Should temporal-field edges exist at all?** Fields like Born, Died, Date, Beginning, End already create temporal facets on the node. The edges created by RelationshipLabels (e.g. `["Date"] = "occurred_at"`) are redundant with the facets. Should we remove those label mappings entirely?

2. **Qualifier parsing complexity** — Values like `"19 BBY–1 BBY, disbanded by Emperor Sheev Palpatine"` have complex structure. How robust does the parser need to be? A regex for `"text (YYYY BBY–YYYY ABY..."` would catch most cases.

3. **Two-pass vs single-pass ETL** — Building the type map requires knowing all node types first. Currently nodes and edges are built in the same loop. We'd need to either build nodes first (separate pass), or maintain a pageId→type lookup during processing.

## Files to Modify

- `src/StarWarsData.Services/InfoboxGraphService.cs` — edge creation logic in `BuildGraphAsync`
- `src/StarWarsData.Models/Entities/RelationshipEdge.cs` — potentially add `qualifier` field
- Re-run ETL on `starwars-dev` after changes
