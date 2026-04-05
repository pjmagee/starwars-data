# Design: Temporal Facets on Knowledge Graph Nodes & Edges

**Status:** Draft
**Date:** 2026-04-02
**Author:** Patrick Magee + Claude

## Problem

The knowledge graph (`kg.nodes`, `kg.edges`) stores temporal data as flat `startYear`/`endYear` integers on nodes, with zero temporal data on edges. This has three problems:

1. **Semantic loss** — A Character born in 22 BBY and a War that began in 22 BBY are indistinguishable. You can't query "who was **alive** during the Clone Wars?" because you can't distinguish lifespan dates from conflict dates from institutional founding dates.

2. **First-win data loss** — The ETL takes only the first matching "start" and "end" field per node. A Government with `Date established`, `Date fragmented`, `Date reorganized`, `Date dissolved`, and `Date restored` loses 3 of 5 temporal events. The Galactic Republic's 25,000-year lifecycle of state transitions collapses to a single start/end pair.

3. **Calendar conflation** — In-universe dates (BBY/ABY) and real-world dates (CE) coexist in the same fields. `Person.Born = "October 23, 1959"` and `Character.Born = "22 BBY"` both map to `startYear`, but one is a galactic calendar sort-key and the other is a Gregorian calendar year. The current parser silently drops real-world dates (returns null).

4. **Edge temporal void** — 652,082 edges have no temporal bounds, despite `fromYear`/`toYear` fields existing on the model. This means graph traversal can't be time-scoped ("show Anakin's relationships as of 19 BBY").

## Current State

### Node temporal fields (ETL: InfoboxGraphService)

```
GraphNode {
    startYear: int?        // first matching StartDateField, parsed to sort-key
    endYear: int?          // first matching EndDateField, parsed to sort-key
    startDateText: string? // original text of the first start field
    endDateText: string?   // original text of the first end field
}
```

### Date field mappings in ETL

**StartDateFields** (→ startYear):
- Born, Date established, Date founded, Date, Beginning, Begin, Constructed, Founding

**EndDateFields** (→ endYear):
- Died, Date dissolved, Date fragmented, End, Destroyed, Date destroyed

**Not captured at all:**
- Date reorganized, Date restored, Date engineered, Date created
- Release date, Publication date, Airdate (out-of-universe)

### Temporal data volumes (from starwars-dev, 2026-04-02)

| Category | Count | Example |
|---|---|---|
| BBY/ABY parseable | 21,248 | `"22 BBY"`, `"By 20 BBY"` |
| BBY/ABY + detail | 4,806 | `"3637 BBY, Rishi"` |
| Real-world dates | 1,751 | `"October 23, 1959"`, `"September 2017"` |
| Vague/relative | 1,219 | `"During the Battle of Yavin"`, `"Prior to the Clone Wars"` |
| Canceled/Unknown | 77 | `"Canceled"`, `"TBA"` |

### Date fields by entity type

| Semantic dimension | Fields | Entity types | Temporal shape |
|---|---|---|---|
| **Lifespan** | Born, Died | Character, Person | Interval (birth→death) |
| **Conflict** | Beginning, End, Date | War, Campaign, Battle, Mission, Duel, Election, Event, Law, Competition | Interval or Point |
| **Construction** | Constructed, Destroyed | Structure, Location, City, SpaceStation, IndividualShip | Interval |
| **Creation** | Date created, Date destroyed, Date engineered | Device, Weapon, Artifact, Lightsaber, Armor, HolocronInfobox, Disease | Interval |
| **Institutional** | Date established/founded, Date dissolved, Date reorganized, Date restored, Date fragmented | Government, Organization, Military_unit, Treaty, TitleOrPosition, Religion, TradeRoute, CulturalGroup | **Lifecycle chain** |
| **Publication** | Release date, Publication date, Airdate | Book, ComicBook, VideoGame, Movie, TelevisionEpisode, etc. | Point (real-world) |

### Lifecycle chain complexity (Government/Organization)

Some entities have rich multi-step lifecycles that a single start/end pair cannot represent:

```
Black Sun: founded(-3644) → fragmented(-33) → reorganized(-31) → dissolved(24) → restored(127)
Galactic Republic: established(-25053) → fragmented(-1100) → reorganized(-1000) → dissolved(-19) → restored(4)
Galactic Empire: established(-19) → fragmented(4) → reorganized(5) → restored(21 as First Order)
```

Field counts in Government type:

- Date established: 116
- Date dissolved: 73
- Date reorganized: 44
- Date fragmented: 28
- Date restored: 18

## Proposed Model

### TemporalFacet (new embedded document)

```csharp
public class TemporalFacet
{
    /// <summary>Original infobox field name, e.g. "Born", "Date established".</summary>
    [BsonElement("field")]
    public string Field { get; set; } = "";

    /// <summary>
    /// Semantic dimension and role. Format: "{dimension}.{role}".
    /// Dimensions: lifespan, conflict, construction, creation, institutional, publication.
    /// Roles: start, end, point, reorganized, restored, fragmented.
    /// Examples: "lifespan.start", "conflict.end", "institutional.reorganized"
    /// </summary>
    [BsonElement("semantic")]
    public string Semantic { get; set; } = "";

    /// <summary>Calendar system: "galactic" (BBY/ABY sort-key) or "real" (CE year).</summary>
    [BsonElement("calendar")]
    public string Calendar { get; set; } = "";

    /// <summary>
    /// Parsed year. For galactic: sort-key (negative=BBY, positive=ABY).
    /// For real: CE year (e.g. 1959, 2017).
    /// Null if unparseable (vague text like "During the Clone Wars").
    /// </summary>
    [BsonElement("year")]
    [BsonIgnoreIfNull]
    public int? Year { get; set; }

    /// <summary>Original text from infobox, preserved for display.</summary>
    [BsonElement("text")]
    public string Text { get; set; } = "";

    /// <summary>Sequence position within a lifecycle chain (0-based). 0 for simple start/end.</summary>
    [BsonElement("order")]
    public int Order { get; set; }
}
```

### Updated GraphNode

```csharp
public class GraphNode
{
    // ... existing fields ...

    // ── Computed lifecycle envelope (kept for fast range queries) ──

    /// <summary>Earliest start-role facet year. For cheap range filtering.</summary>
    public int? StartYear { get; set; }

    /// <summary>Latest end-role facet year.</summary>
    public int? EndYear { get; set; }

    // ── Rich temporal data ──

    /// <summary>All temporal facets, preserving semantic meaning and lifecycle order.</summary>
    public List<TemporalFacet> TemporalFacets { get; set; } = [];

    // startDateText / endDateText can be REMOVED — the text lives in facets now
}
```

### Semantic mapping table

This is the lookup the ETL uses to classify each infobox field:

```csharp
static readonly Dictionary<string, (string semantic, string calendar)> TemporalFieldMap = new()
{
    // Lifespan
    ["Born"]              = ("lifespan.start", "auto"),     // auto = detect from text
    ["Died"]              = ("lifespan.end", "auto"),

    // Conflict
    ["Beginning"]         = ("conflict.start", "galactic"),
    ["Begin"]             = ("conflict.start", "galactic"),
    ["End"]               = ("conflict.end", "galactic"),
    ["Date"]              = ("conflict.point", "galactic"),  // Battle, Mission, Event, etc.

    // Construction
    ["Constructed"]       = ("construction.start", "galactic"),
    ["Destroyed"]         = ("construction.end", "galactic"),

    // Creation
    ["Date created"]      = ("creation.start", "galactic"),
    ["Date destroyed"]    = ("creation.end", "galactic"),
    ["Date engineered"]   = ("creation.start", "galactic"),

    // Institutional lifecycle
    ["Date established"]  = ("institutional.start", "galactic"),
    ["Date founded"]      = ("institutional.start", "galactic"),
    ["Founding"]          = ("institutional.start", "galactic"),
    ["Date dissolved"]    = ("institutional.end", "galactic"),
    ["Date reorganized"]  = ("institutional.reorganized", "galactic"),
    ["Date restored"]     = ("institutional.restored", "galactic"),
    ["Date fragmented"]   = ("institutional.fragmented", "galactic"),

    // Publication (always real-world)
    ["Release date"]      = ("publication.release", "real"),
    ["Publication date"]  = ("publication.release", "real"),
    ["Airdate"]           = ("publication.release", "real"),
};
```

### Calendar detection ("auto" mode)

For fields like "Born" that can be either BBY/ABY (Character) or real-world (Person):

```
1. Try BBY/ABY regex → if match, calendar = "galactic", year = sort-key
2. Try real-world date parse (month/day/year or just year) → if match, calendar = "real", year = CE year
3. Neither matches → calendar = "unknown", year = null, text preserved
```

### Lifecycle ordering

For institutional entities with multiple temporal events, `Order` is assigned by parsing the year and sorting chronologically. If year is null, order is assigned by the infobox field order (document position).

### Document examples

**Character (simple interval):**
```json
{
  "name": "Aayla Secura",
  "type": "Character",
  "startYear": -48,
  "endYear": -19,
  "temporalFacets": [
    { "field": "Born", "semantic": "lifespan.start", "calendar": "galactic", "year": -48, "text": "c. 48 BBY", "order": 0 },
    { "field": "Died", "semantic": "lifespan.end", "calendar": "galactic", "year": -19, "text": "19 BBY, Felucia", "order": 0 }
  ]
}
```

**Person (real-world dates):**
```json
{
  "name": "Adam Driver",
  "type": "Person",
  "startYear": 1983,
  "endYear": null,
  "temporalFacets": [
    { "field": "Born", "semantic": "lifespan.start", "calendar": "real", "year": 1983, "text": "November 19, 1983 in", "order": 0 }
  ]
}
```

**Government (lifecycle chain):**
```json
{
  "name": "Galactic Republic",
  "type": "Government",
  "startYear": -25053,
  "endYear": -19,
  "temporalFacets": [
    { "field": "Date established", "semantic": "institutional.start", "calendar": "galactic", "year": -25053, "text": "25,053 BBY", "order": 0 },
    { "field": "Date fragmented", "semantic": "institutional.fragmented", "calendar": "galactic", "year": -1100, "text": "1100 BBY", "order": 1 },
    { "field": "Date reorganized", "semantic": "institutional.reorganized", "calendar": "galactic", "year": -1000, "text": "1000 BBY, Ruusan Reformation", "order": 2 },
    { "field": "Date dissolved", "semantic": "institutional.end", "calendar": "galactic", "year": -19, "text": "19 BBY", "order": 3 },
    { "field": "Date restored", "semantic": "institutional.restored", "calendar": "galactic", "year": 4, "text": "4 ABY, as the New Republic", "order": 4 }
  ]
}
```

**Battle (point event):**
```json
{
  "name": "Battle of Yavin",
  "type": "Battle",
  "startYear": 0,
  "endYear": 0,
  "temporalFacets": [
    { "field": "Date", "semantic": "conflict.point", "calendar": "galactic", "year": 0, "text": "0 BBY", "order": 0 }
  ]
}
```

**VideoGame (out-of-universe publication):**
```json
{
  "name": "Star Wars: Battlefront",
  "type": "VideoGame",
  "startYear": 2015,
  "endYear": null,
  "temporalFacets": [
    { "field": "Release date", "semantic": "publication.release", "calendar": "real", "year": 2015, "text": "November 17, 2015", "order": 0 }
  ]
}
```

## Edge Temporal Enrichment

### Current state

`RelationshipEdge` has `fromYear`/`toYear` fields (int?, BsonIgnoreIfNull) but they are never populated. 652k edges, all null.

### Proposed: derive edge temporal bounds from node lifecycles

For many edges, the temporal validity can be inferred without LLM:

- **member_of**: overlap of member's lifespan and organization's existence
- **battles_in**: the battle's date
- **born_on**: the character's birth year (point-in-time)
- **led_by**: overlap of leader's tenure and organization's existence

Strategy:
1. After node facets are populated, run a second pass over edges
2. For each edge, look up source and target node lifecycle envelopes
3. Compute `fromYear = max(source.startYear, target.startYear)` and `toYear = min(source.endYear, target.endYear)` where both exist
4. Label-specific overrides (e.g., `born_on` → use only the lifespan.start facet year)

This is a **separate phase** — get node facets right first.

## Index Strategy

```javascript
// Semantic queries: "all entities with lifespan.start in year range"
{ "temporalFacets.semantic": 1, "temporalFacets.year": 1 }

// Calendar-scoped: "all galactic-calendar events in range"
{ "temporalFacets.calendar": 1, "temporalFacets.year": 1 }

// Type + lifecycle envelope (fast range filter, existing pattern)
{ "type": 1, "startYear": 1, "endYear": 1 }

// Edge temporal traversal
{ "fromId": 1, "fromYear": 1, "toYear": 1 }
```

## Query Examples Enabled

| Question | Current capability | With facets |
|---|---|---|
| What existed during 22-19 BBY? | Yes (startYear/endYear overlap) | Yes, same |
| Who was **alive** during 22-19 BBY? | No (can't distinguish lifespan from other dates) | `temporalFacets: { semantic: "lifespan.*", year overlap }` |
| What wars were happening in 0 BBY? | No (conflict.start vs lifespan.start indistinguishable) | `type: "War", temporalFacets: { semantic: "conflict.*" }` |
| When was the Galactic Republic reorganized? | No (only start/end stored) | `temporalFacets: { semantic: "institutional.reorganized" }` |
| What books were published in 2015? | No (real-world dates drop to null) | `temporalFacets: { calendar: "real", semantic: "publication.*", year: 2015 }` |
| Show Anakin's relationships as of 19 BBY | No (edges have no temporal bounds) | Edge fromYear/toYear from node overlap |

## Implementation Plan

### Phase 1: Node temporal facets

1. Add `TemporalFacet` class to Models
2. Add `TemporalFacets` list to `GraphNode`
3. Update `InfoboxGraphService.BuildGraphAsync` to populate facets
4. Add real-world date parser alongside existing BBY/ABY parser
5. Compute lifecycle envelope from facets (replaces first-win logic)
6. Re-run ETL on `starwars-dev`

### Phase 2: Indexes and queries

1. Create compound indexes on `starwars-dev`
2. Update `GraphRAGToolkit` with semantic temporal query tools
3. Update Knowledge Graph page to display facets with semantic labels

### Phase 3: Edge temporal derivation

1. Post-processing pass to derive edge `fromYear`/`toYear` from node facets
2. Label-specific temporal inference rules
3. Add edge temporal indexes

### Phase 4: Vague date resolution (future/optional)

- Resolve relative text ("During the Clone Wars" → -22 to -19) by linking to referenced events
- This requires entity linking from text, potentially LLM-assisted

## Open Questions

1. **Lifecycle envelope for multi-calendar entities**: If a Person has `calendar: "real"` facets, should `startYear`/`endYear` use CE years? This means the envelope would mix calendar systems across nodes. Alternative: two envelopes (`galacticStart`/`galacticEnd` + `realStart`/`realEnd`).

2. **"Date" field ambiguity**: The field "Date" appears on Battle, Mission, Event, Duel, Election, Law, Holiday, Competition, War, and Campaign. It's classified as `conflict.point` but some of these (Holiday, Law) aren't really conflicts. Should "Date" on non-conflict types get a different semantic (e.g., `event.point`)?

3. **Vague dates**: 1,219 values like "During the Battle of Yavin" carry temporal information. Should we store these as facets with `year: null` and attempt resolution later? Or skip them entirely?

4. **Multiple values per field**: Some fields have multiple values (e.g., `Date reorganized: ["c. 21 BBY", "19 BBY"]`). Each should become a separate facet with incremented `Order`.

5. **"Year introduced" field**: 318 values across vehicle/droid templates. This is a product introduction date — maps to `creation.start` but for manufactured product lines rather than individual items. Notably present on AquaticVehicle (145), ShipSeries (61), RepulsorliftVehicle (many), etc.

## Gap Analysis — Full Template Coverage Report

**126 total infobox templates** in raw.pages. Queried every template for all known and candidate temporal fields.

### Coverage overview

| Category | Templates | Pages | Temporal values |
| --- | --- | --- | --- |
| Has temporal fields (currently captured by ETL) | ~35 | ~90k | ~24k |
| Has temporal fields (NOT captured — gaps) | ~62 additional | ~30k | ~19k+ |
| No temporal fields at all | 29 | ~46k | 0 |
| **Total** | **126** | **~166k** | **~43k+** |

### Templates with NO temporal fields (29 templates, ~46k pages)

These are inherently non-temporal entities or lack date fields in their infoboxes:

| Template | Pages | Notes |
| --- | --- | --- |
| System | 11,601 | Star system — spatial, not temporal |
| CelestialBody | 8,631 | Planets/moons — no date fields in infobox |
| Species | 6,431 | Biological species — no temporal fields |
| Sector | 1,295 | Galactic sector — spatial |
| Year | 1,072 | Year article — IS a temporal entity but has no date fields |
| Plant | 716 | Flora — no dates |
| Star | 590 | Stellar body |
| Language | 442 | No dates |
| ForcePower | 289 | No dates |
| Nebula | 226 | Spatial |
| AquaticVehicle | 145 | Has `Year introduced` (see below) |
| StarCluster | 88 | Spatial |
| Script | 67 | Writing system |
| Region | 66 | Spatial |
| ShipSeries | 61 | Has `Year introduced` (see below) |
| LightsaberForm | 53 | Combat style |
| Era | 45 | HAS temporal meaning but stored in `Years` field, not standard date |
| RacingTrack | 35 | Location |
| Sport | 33 | No dates |
| SpaceStationClass | 33 | Vehicle class spec |
| AirVehicle | 16 | Has `Year introduced` |
| Vehicle | 15 | No dates |
| StarType | 14 | Stellar classification |
| Currency | 12 | Has `Time of use` (vague temporal, 3 values) |
| Galaxy | 7 | Spatial |
| Constellation | 5 | Spatial |
| InstructionalCourse | 3 | No dates |
| VideoGameSeries | 2 | Has `First/Last game published` (3 values) |
| FilmingLocation | 1 | Has `First seen in` (1 value, not a date) |

**Note:** AquaticVehicle, ShipSeries, and AirVehicle have `Year introduced` (318 total across templates) which IS temporal but wasn't in our scan. These should move to the "has temporal" category.

### Complete template × temporal field matrix (97 templates with temporal data)

Sorted by total temporal values. Fields marked with * are NOT currently captured by the ETL.

| Template | Pages | Temporal fields (count) | Total |
| --- | --- | --- | --- |
| Character | 43,733 | Born (1,941), Died (10,129) | 12,070 |
| Battle | 4,185 | Date (3,905) | 3,905 |
| ComicBook | 2,106 | Publication date* (2,094) | 2,094 |
| Organization | 4,654 | Date founded (784), Date dissolved (519), Date fragmented (209), Date reorganized (192), Date restored (63) | 1,767 |
| Person | 10,349 | Born (1,319), Died (330) | 1,649 |
| MagazineArticle | 1,545 | Release date* (1,545) | 1,545 |
| ReferenceMagazine | 1,303 | First released* (1,303), International releases* (141) | 1,444 |
| Mission | 1,466 | Date (1,403) | 1,403 |
| Book | 1,214 | Release date* (1,178), Rerelease date(s)* (32), International release date(s)* (29), Early release date* (6) | 1,245 |
| IndividualShip | 4,911 | Destroyed (1,113), Commissioned* (100), Retired* (10) | 1,223 |
| TelevisionEpisode | 1,108 | Air date* (1,105), International release* (42) | 1,147 |
| Structure | 5,858 | Destroyed (576), Constructed (530), Rebuilt* (12) | 1,118 |
| MagazineIssue | 1,092 | Release date* (1,085) | 1,085 |
| Droid | 3,358 | Date destroyed (931), Date created (85) | 1,016 |
| ShortStory | 887 | Release date* (887) | 887 |
| ReferenceBook | 876 | Release date* (867) | 867 |
| ComicStory | 769 | Publication date* (769) | 769 |
| Event | 903 | Date (705) | 705 |
| ComicMagazine | 696 | Release date* (696) | 696 |
| Location | 5,809 | Destroyed (451), Constructed (228) | 679 |
| ComicCollection | 619 | Publication date* (616), Alternate release* (56) | 672 |
| Military_unit | 1,868 | Date founded (270), Date dissolved (228), Date fragmented (88), Date reorganized (71), Date restored (12) | 669 |
| ExpansionPack | 658 | Publication date* (658) | 658 |
| Audiobook | 504 | Release date* (504), Rerelease date(s)* (109) | 613 |
| Adventure | 613 | Release date* (595) | 595 |
| War | 372 | Beginning (226), End (196), Date (93) | 515 |
| Campaign | 346 | Begin (195), End (167), Date (124) | 486 |
| ComicSeries | 233 | Start date* (230), End date* (214) | 444 |
| VideoGame | 376 | Release date* (365), Date closed* (22) | 387 |
| ActivityBook | 361 | Release date* (361) | 361 |
| ComicArc | 175 | Start date* (175), End date* (173) | 348 |
| City | 1,589 | Destroyed (163), Constructed (105), Rebuilt* (11) | 279 |
| Government | 358 | Date established (116), Date dissolved (73), Date reorganized (44), Date fragmented (28), Date restored (18) | 279 |
| TitleOrPosition | 1,504 | Date established (130), Date suspended* (71), Date abolished* (51), Date reestablished* (26) | 278 |
| WebArticle | 269 | Release date* (269) | 269 |
| IU_media | 1,156 | Release date* (237) | 237 |
| Lightsaber | 515 | Date created (140), Date destroyed (55), Date discovered* (26) | 221 |
| Duel | 226 | Date (214) | 214 |
| RealCompany | 295 | Founded* (177), Dissolved* (21) | 198 |
| BookSeries | 115 | First book published* (112), Last book published* (81) | 193 |
| Company | 1,869 | Founding (116), Date dissolved (64) | 180 |
| Artifact | 582 | Date created (64), Date discovered* (62), Date destroyed (53) | 179 |
| SpaceStation | 675 | Destroyed (127), Constructed (43), Retired* (8) | 178 |
| MagazineSeries | 92 | Start date* (85), End date* (68) | 153 |
| TradingCardSet | 151 | Publication date* (151) | 151 |
| Fleet | 469 | Founding (92), Fragmentation* (32), Reorganization* (26) | 150 |
| Weapon | 3,835 | Date created (77), Date destroyed (39), Date discovered* (16) | 132 |
| TelevisionSeries | 84 | First aired* (81), Last aired* (46) | 127 |
| Soundtrack | 126 | Release date* (126) | 126 |
| ToyLine | 90 | Start date* (90), End date* (31) | 121 |
| Device | 3,957 | Date created (69), Date destroyed (34), Date discovered* (16) | 119 |
| TelevisionSeason | 48 | First aired* (48), Last aired* (37) | 85 |
| Religion | 183 | Date founded (46), Date of collapse* (32), Date of restoration* (7) | 85 |
| Music | 151 | Released* (73) | 73 |
| BoardGame | 73 | Release date* (73) | 73 |
| Movie | 90 | Released* (71) | 71 |
| Documentary | 70 | Release date* (70) | 70 |
| GraphicNovel | 71 | Publication date* (65) | 65 |
| MagazineDepartment | 177 | First issue* (38), Last issue* (18) | 56 |
| ComicStrip | 56 | Publication date* (56) | 56 |
| Webstrip | 42 | First published* (40), Last published* (12) | 52 |
| DroidSeries | 1,599 | Date introduced* (39), Date retired* (13) | 52 |
| Family | 633 | Fragmented* (48), Restored* (3) | 51 |
| Law | 91 | Date (51) | 51 |
| TradeRoute | 353 | Date founded (47), Date dissolved (1) | 48 |
| HolocronInfobox | 95 | Date created (21), Date discovered* (21), Date destroyed (6) | 48 |
| Tactics | 122 | Date created (26), First employed* (15), Last employed* (3) | 44 |
| Holiday | 95 | Date (29), Begins* (10), Ends* (4) | 43 |
| Armor | 640 | Date created (26), Date destroyed (12), Date discovered* (5) | 43 |
| Treaty | 60 | Date established (38), Date dissolved (4) | 42 |
| Medal | 177 | First awarded* (25), Last awarded* (15) | 40 |
| HomeVideo | 38 | Release date* (38) | 38 |
| Store | 336 | Established* (27), Closed* (11) | 38 |
| LiveShow | 23 | Premiere date* (23), Closing date* (3) | 26 |
| RealEvent | 20 | Start date* (20), End date* (6) | 26 |
| GroundVehicle | 403 | Retired* (23) | 23 |
| Media | 23 | Release date* (23) | 23 |
| StarshipClass | 2,330 | Retired* (22) | 22 |
| Election | 21 | Date (21) | 21 |
| TabletopGame | 20 | Publication date* (20) | 20 |
| Disease | 110 | Date engineered (15) | 15 |
| Food | 2,385 | Date created (11) | 11 |
| Clothing | 449 | Date created (8), Date destroyed (2), Date discovered* (1) | 11 |
| Substance | 792 | Date created (5), Date discovered* (5) | 10 |
| AudiobookSeries | 5 | First published* (5), Last published* (5) | 10 |
| RepulsorliftVehicle | 1,243 | Retired* (7) | 7 |
| FanPodcast | 6 | Founded* (6) | 6 |
| Game | 114 | First played* (5) | 5 |
| IndividualVehicle | 23 | Destroyed (5) | 5 |
| Website | 9 | Launched* (5) | 5 |
| Deity | 180 | First worshipped* (5) | 5 |
| Band | 111 | Reorganized* (2), Dissolved* (1), Fragmented* (1) | 4 |
| CulturalGroup | 34 | Date established (1), Date dissolved (2) | 3 |
| Multimedia | 3 | First media published* (3) | 3 |
| FanOrganization | 3 | Founded* (3) | 3 |
| Competition | 9 | Date (3) | 3 |
| Symbol | 20 | Date created (1) | 1 |

**Additionally not in the above matrix** (found in "no temporal" templates):
- `Year introduced`: 318 values across AquaticVehicle, ShipSeries, AirVehicle, RepulsorliftVehicle, etc.
- `First game published` / `Last game published`: 3 values (VideoGameSeries)
- `Time of use`: 3 values (Currency — vague temporal, e.g. "High Republic Era")

Systematic scan of all infobox field labels in `raw.pages` matching temporal patterns. Compared against the current `StartDateFields` + `EndDateFields` + `AlwaysProperties` in `InfoboxGraphService.cs`.

### Currently captured (in ETL mapping)

| Field | ETL role | Count in raw.pages |
|---|---|---|
| Born | start | 3,260 |
| Died | end | 10,459 |
| Date | start | 6,548 |
| Beginning | start | 226 |
| Begin | start | 195 |
| End | end | 363 |
| Date established | start | 285 |
| Date founded | start | 1,147 |
| Founding | start | 208 |
| Date dissolved | end | 891 |
| Date fragmented | end | 325 |
| Constructed | start | 906 |
| Destroyed | end | 2,435 |
| Date created | AlwaysProperties | 533 |
| Date destroyed | end | 1,132 |
| Date engineered | AlwaysProperties | 15 |
| Date reorganized | AlwaysProperties | 307 |
| Date restored | AlwaysProperties | 93 |

### NOT captured — in-universe temporal fields (GAPS)

These exist in raw.pages but are **not** in `StartDateFields`, `EndDateFields`, or have no facet mapping:

| Field | Count | Template(s) | Proposed semantic | Notes |
|---|---|---|---|---|
| **Commissioned** | 100 | IndividualShip | `construction.start` | Ship put into service |
| **Retired** | 70 | GroundVehicle, etc. | `construction.end` | Vehicle/ship decommissioned |
| **Date discovered** | 152 | Device | `creation.discovered` | New semantic role |
| **Date suspended** | 71 | TitleOrPosition | `institutional.suspended` | New role — position suspended |
| **Date abolished** | 51 | TitleOrPosition | `institutional.end` | Position abolished |
| **Date reestablished** | 26 | TitleOrPosition | `institutional.restored` | Maps to existing restored role |
| **Date introduced** | 39 | DroidSeries | `creation.start` | Product introduction |
| **Date retired** | 13 | DroidSeries | `creation.end` | Product retirement |
| **Date of collapse** | 32 | Religion | `institutional.end` | Collapse = dissolution |
| **Date of restoration** | 7 | Religion | `institutional.restored` | Restoration |
| **Fragmented** | 49 | Family | `institutional.fragmented` | Same meaning, different label |
| **Fragmentation** | 32 | Fleet | `institutional.fragmented` | Noun form of same concept |
| **Reorganization** | 26 | Fleet | `institutional.reorganized` | Noun form |
| **Reorganized** | 2 | Band | `institutional.reorganized` | Past tense form |
| **Rebuilt** | 23 | City | `construction.rebuilt` | New role — rebuilt after destruction |
| **Established** | 27 | Store | `institutional.start` | Synonym of Date established |
| **Dissolved** | 22 | RealCompany | `institutional.end` | Synonym (real-world company) |
| **Closed** | 11 | Store | `institutional.end` | Store closed |
| **Date closed** | 22 | VideoGame | `publication.closed` | Online game shutdown (real-world) |
| **Restored** | 3 | Family | `institutional.restored` | Synonym |
| **Begins** | 10 | Holiday | `conflict.start` | Synonym of Beginning |
| **Ends** | 4 | Holiday | `conflict.end` | Synonym of End |
| **Founded** | 186 | FanOrganization | `institutional.start` | Real-world founding (CE year) |
| **Launched** | 5 | Website | `publication.launch` | Website launch (real-world) |
| **Closing date** | 3 | LiveShow | `publication.end` | Show closing date |

**Total gap: ~1,046 temporal values not captured by current ETL.**

### NOT captured — out-of-universe / publication temporal fields (GAPS)

| Field | Count | Template(s) | Proposed semantic | Calendar |
|---|---|---|---|---|
| **Release date** | 8,919 | Many media types | `publication.release` | real |
| **Publication date** | 4,429 | ComicBook, etc. | `publication.release` | real |
| **Air date** | 1,105 | TelevisionEpisode | `publication.release` | real |
| **First released** | 1,303 | ReferenceMagazine | `publication.release` | real |
| **Start date** | 600 | MagazineSeries | `publication.start` | real |
| **End date** | 492 | MagazineSeries | `publication.end` | real |
| **First aired** | 129 | TelevisionSeason | `publication.start` | real |
| **Last aired** | 83 | TelevisionSeason | `publication.end` | real |
| **First book published** | 112 | BookSeries | `publication.start` | real |
| **Last book published** | 81 | BookSeries | `publication.end` | real |
| **Premiere date** | 23 | LiveShow | `publication.release` | real |
| **Released** | 144 | Movie | `publication.release` | real |
| **First published** | 45 | Webstrip | `publication.start` | real |
| **Last published** | 17 | Webstrip | `publication.end` | real |
| **Rerelease date(s)** | 141 | Audiobook | `publication.rerelease` | real |
| **International releases** | 141 | ReferenceMagazine | `publication.international` | real |
| **International release** | 42 | TelevisionEpisode | `publication.international` | real |
| **International release date(s)** | 29 | Book | `publication.international` | real |
| **Alternate release** | 56 | ComicCollection | `publication.alternate` | real |
| **Early release date** | 6 | Book | `publication.early` | real |
| **First issue** | 38 | MagazineDepartment | `publication.start` | real |
| **Last issue** | 18 | MagazineDepartment | `publication.end` | real |
| **First media published** | 3 | Multimedia | `publication.start` | real |

**Total publication gap: ~18,156 temporal values not captured.**

### Not temporal (false positives to exclude)

These matched the regex scan but are **not** date fields:

| Field | Count | Reason |
|---|---|---|
| Gender | 34,369 | Contains "ender" — not temporal |
| Publisher / Publisher(s) | 13,779 | Entity, not date |
| Published in | 4,074 | Publication name, not date |
| Found / Found on | 1,542 | Location discovered, not a date |
| Founder / Founder(s) / Founded by | 906 | Entity, not date |
| Creator / Creators / Creator(s) / Created by | 1,358 | Entity, not date |
| Established by | 238 | Entity, not date |
| End points | 281 | Trade route endpoints (locations) |
| Founding document | 18 | Document name, not date |
| First member(s) | 27 | Entity, not date |
| Candidates | 21 | Entity list |
| Various war/conflict names | ~35 | Section headers accidentally captured |

### Summary

| Category | Fields captured | Fields missed | Values missed |
|---|---|---|---|
| In-universe temporal | 18 | 24 | ~1,046 |
| Out-of-universe publication | 0 | 23 | ~18,156 |
| **Total** | **18** | **47** | **~19,202** |

The current ETL captures **18 temporal field labels**. There are at least **47 additional labels** carrying temporal data in the raw infobox corpus, representing ~19,200 values. The largest gaps are in out-of-universe publication dates (which are entirely ignored) and institutional lifecycle synonyms (Fragmented vs Date fragmented, Established vs Date established, etc.).

### Recommended field normalization

Many gaps are just synonyms. The ETL should normalize these to canonical forms before semantic classification:

```
Commissioned, Constructed, Rebuilt → construction.*
Retired, Destroyed → construction.end
Date abolished, Dissolved, Closed, Date closed, Date of collapse → institutional.end
Established, Founded, Date established, Date founded, Founding → institutional.start
Date reestablished, Restored, Date of restoration, Date restored → institutional.restored
Fragmented, Fragmentation, Date fragmented → institutional.fragmented
Reorganized, Reorganization, Date reorganized → institutional.reorganized
Date suspended → institutional.suspended (new role)
Date discovered → creation.discovered (new role)
Date introduced → creation.start
Date retired → creation.end
Begins, Beginning, Begin → conflict.start (or event.start depending on type)
Ends, End → conflict.end (or event.end depending on type)
Release date, Publication date, Air date, First released, Released,
  Premiere date, First aired, First published, First book published,
  First media published, Launched → publication.release / publication.start
Last aired, Last published, Last book published, Last issue,
  Closing date, Date closed, End date → publication.end
Rerelease date(s), Alternate release, International releases,
  International release, International release date(s),
  Early release date → publication.variant (secondary release events)
```

## To Revisit: Explicit vs Derived Edge Temporal Bounds

**Added:** 2026-04-04
**Status:** Known limitation, not yet addressed

Edges in `kg.edges` carry `fromYear` / `toYear` temporal bounds, populated via two paths:

1. **Explicit** — parsed from parenthetical qualifiers in infobox values by `ExtractPrimaryLinks`, e.g. `"Darth Plagueis (84 BBY–32 BBY)"` → `fromYear: -84, toYear: -32`. These are the **actual relationship window** as stated in the source.

2. **Derived** — computed by `InfoboxGraphService.BuildGraphAsync` as the overlap of source and target node lifespans when the edge has no explicit qualifier. E.g. the `Ahsoka Tano → apprentice_of → Anakin Skywalker` edge has no parenthetical in the infobox, so the builder sets `fromYear = max(ahsokaStart, anakinStart) = -36` and `toYear = min(ahsokaEnd, anakinEnd) = -20`. This is a **valid bounding envelope** (the edge cannot exist outside the overlap) but not the actual relationship window — the apprenticeship was ~3 years (22 BBY → 19 BBY), not 16.

### Why this matters

After the 2026-04-04 KG rebuild, the distribution is:

- **648,314** total edges
- **325,925** (50%) have `fromYear` stored
- Most are **derived** from lifecycle overlap, not explicit from parenthetical qualifiers

Consumers cannot distinguish explicit from derived. A query like "who was apprenticed in 30 BBY" would false-positive on Ahsoka Tano because her derived envelope covers 36–20 BBY even though the actual apprenticeship was 22–19 BBY.

The `Evidence` field contains the parenthetical qualifier for explicit edges (e.g. `"Infobox field 'Masters': Darth Plagueis (84 BBY-32 BBY)"`) but that's unstructured — analytics tools can't filter on it.

### Proposed fix

Add a confidence/provenance flag to `RelationshipEdge`:

```csharp
/// <summary>
/// True if FromYear/ToYear were parsed directly from the infobox value's
/// parenthetical qualifier. False if derived from the overlap of source
/// and target node lifespans (a broader bounding envelope).
/// </summary>
[BsonElement("temporalExplicit")]
[BsonIgnoreIfDefault]
public bool TemporalExplicit { get; set; }
```

Then:

- Analytics tools can filter `{ temporalExplicit: true }` for precise temporal queries
- Derived bounds remain useful for "could this have happened?" questions (they're necessarily true as outer bounds)
- The KG builder sets the flag in the `ExtractPrimaryLinks` path vs the lifecycle-overlap derivation path
- Downstream tools (KGAnalyticsToolkit, render_graph time-scoping) gain an explicit confidence dimension

**Dependencies:** None — additive field, no schema migration needed beyond rebuilding the KG.

**Next action:** When touching edge temporal queries (e.g. the proposed `count_related_by_year_range` tool for character-level time-series), add the flag at the same time so the new tool can use it for precise date filtering.
