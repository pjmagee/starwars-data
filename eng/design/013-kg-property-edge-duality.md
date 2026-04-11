# Design 013: KG-as-Single-Source-of-Truth + Tool Routing Fix

**Status:** Partially shipped 2026-04-11 (dropped-fields fallthrough). Tool routing fix deferred.
**Date:** 2026-04-11
**Owner:** Patrick Magee

> **Reading guide.** This document started as a proposal for property/edge duality after a coverage check appeared to show massive data loss in `kg.nodes.properties`. That framing was wrong. After re-running Phase 5 and cross-checking `kg.edges`, every field I thought was "dropped" turned out to already exist in the edge graph under a normalized label name. The real issue is tool-layer routing, not data shape. The sections below reflect the corrected picture. The original duality proposal is captured in [What we tried first and why it was wrong](#what-we-tried-first-and-why-it-was-wrong) near the bottom as a lessons-learned.

## North star (Patrick, 2026-04-11)

1. **KG (`kg.nodes` + `kg.edges`) is the single runtime source of truth for structured queries.** `raw.pages` is an ETL artifact; semantic vector search (article chunks) is the only other runtime data source. Over time, Pages-side tools get deleted as KG reaches coverage parity.
2. **Future enrichment happens via LLM extraction** (agent reads article prose, emits additional edges/properties) — the existing Phase 6 batch pipeline extended over time. This fills in gaps the deterministic parser can't see. Orthogonal to the work in this doc.

## What I found

The Ask agent made a failing tool call: `search_pages_by_property(infoboxType="Food", label="Origin", value="Corellia")` → `[]`. That kicked off an investigation into whether `kg.nodes.properties` was actually covering the raw infobox labels. The first pass looked alarming:

| Type | Field | raw.pages | kg.nodes.properties | Naive conclusion |
| --- | --- | --- | --- | --- |
| Character | Species | 34,708 | 48 | "99.86% data loss" (wrong) |
| Character | Homeworld | 12,804 | 16 | "99.87% data loss" (wrong) |
| Character | Affiliation(s) | 30,975 | 261 | "99.16% data loss" (wrong) |
| Food | Place of origin | 374 | 0 | "100% data loss" (wrong) |
| Food | Race | 117 | 0 | "100% data loss" (wrong) |

The `kg.nodes.properties` column reflects only the residual cases where the KG builder failed to resolve a wiki link and fell through to the property fallback. The overwhelming majority of these fields had already been promoted to edges — which is the whole point of the KG. Checking `kg.edges` tells the real story:

| Raw field | raw count | Edge label | Edge count | Actual coverage |
| --- | --- | --- | --- | --- |
| Character.Species | 34,708 | `species` | **34,913** | 100%+ (multi-link expansion) |
| Character.Affiliation(s) | 30,975 | `affiliated_with` | **51,996** | 168% (multi-affiliation entries) |
| Character.Homeworld | 12,804 | `homeworld` | **12,886** | 100%+ |
| Character.Parent(s) / Children / Siblings / Partner(s) / Masters / Apprentices | full | `parent_of` / `child_of` / `sibling_of` / `partner_of` / `apprentice_of` / `master_of` | full | 100%+ |
| Character.Genetic donor(s) | 979 | `cloned_from` | **980** | 100%+ |
| Character.Caste / Domain / Songs / Genres / Collaborations | 39 / 44 / 24 / 7 / 3 | `caste` / `domain` / `performs_song` / `genres` / `collaborates_with` | **41 / 45 / 36 / 7 / 3** | 100%+ |
| Battle.Place / Conflict / side / commanders / Previous / Next | full | normalized edge labels | full | 100%+ |
| Food.Place of origin | 374 | `originates_from` | **380** | 100%+ |
| Food.Race | 117 | `race` | **115** | ~100% |
| Food.Creator | 103 | `created_by` | **106** | 100%+ |
| Food.Found / Edible by / Potable by / Dishes / Inedible by / Affiliation | full | `found_at` / `edible_by` / `potable_by` / `has_dish` / `inedible_by` / `affiliated_with` | full | 100%+ |

**Everything is in the KG.** Edge counts are often higher than raw counts because one infobox entry with multiple `[[links]]` expands into multiple edges — which means the KG is *richer* than `raw.pages`, not poorer. The deterministic builder does its job correctly.

## The actual bug

The agent doesn't know where the data lives. When it sees a question like *"Distribution of Star Wars characters by species"* it pattern-matches "by Species" → "property" → calls `count_nodes_by_property(type=Character, property=Species)`. That tool queries `kg.nodes.properties` and returns 48 sparse residuals. **The number is confidently wrong and there is no signal back to the agent that it picked the wrong tool.**

The correct call is `group_entities_by_connection(sourceType=Character, label=species)` which queries `kg.edges` and returns the real distribution (`{Human: 28k+, Wookiee: ~200, ...}`). The aggregation tool already exists, works perfectly, and has been sitting in KGAnalyticsToolkit the whole time. The routing is just wrong.

Same pattern applies to:

- `count_nodes_by_properties` (multi-property variant) — same silent under-count for any link-bearing grouping dimension.
- `count_property_for_related_entities` — when `property` is itself a link-bearing field, same bug.
- `get_entity_properties` — when the caller expects a link-bearing field in the returned dict, it won't be there.

## Decision

**Do not touch data shape.** KG is already correct. Do *not* store property duplicates for link-bearing fields — that would be duplicate storage for no benefit given the edge graph exists.

**Fix tool routing instead.** Two layers of fix:

### Layer A — Self-correcting tool responses

When `count_nodes_by_property` returns suspiciously few results compared to the type's total entity count, run one cheap counter-query against `kg.edges` for a matching edge label (via name normalization, e.g. `"Species"` → `"species"`, `"Place of origin"` → `"place_of_origin"` or `"originates_from"`). If the edge count dwarfs the property count, return a structured hint:

```json
{
  "results": [...],
  "note": "Property 'Species' has 48 values on Character nodes. The field is stored as edge label 'species' in kg.edges (34913 edges). Call group_entities_by_connection(sourceType='Character', label='species') for the real distribution."
}
```

Same self-correcting hint pattern that already ships on `search_pages_by_property`.

Apply to: `count_nodes_by_property`, `count_nodes_by_properties`, `count_property_for_related_entities`. The hint should include the specific edge label name so the agent can make the redirect call without a separate discovery step.

### Layer B — Sharpened tool descriptions

Update the `[Description]` attributes on these tools so the model sees the routing rule at decision time:

- **`count_nodes_by_property`** — *"Only use this for SCALAR fields that have no wiki link (Gender, Height, Eye color, Date, Outcome, etc.). For link-bearing fields (Species, Homeworld, Affiliation, Place, commanders, Race, Creator, origin, etc.) the data is stored as edges in `kg.edges`. Use `group_entities_by_connection` or `count_related_entities` instead."*
- **`group_entities_by_connection`** — *"This is the primary tool for any aggregation grouped by a linked entity. If you would have called `count_nodes_by_property` for a field that points to another entity (Species, Homeworld, Affiliation, Place of origin, etc.), call this instead."*
- **`count_related_entities`**, **`count_property_for_related_entities`** — similar sharpening.

The two layers work together: the description teaches the agent what to reach for; the self-correcting hint recovers cases where it reaches wrong anyway.

## What shipped 2026-04-11

**One small change in the builder** at [InfoboxGraphService.cs:222-231](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs#L222-L231): an explicit fallthrough branch for fields with **no property definition, no relationship definition, and no links**. This catches the pure-plain-text residual case (e.g. `Food.Race` with no wiki link: 2 entries, `Food.Other nutritional info` with no link: 8 entries). Without the fix these would be silently dropped; with the fix they land in `kg.nodes.properties`.

```csharp
else if (!hasLabelDef && links.Count == 0)
{
    // Fallthrough: no property def, no rel def, no temporal, no links.
    // Preserve the raw infobox text so it's not silently lost.
    if (values.Count > 0)
        properties[label] = values;
}
```

This is correct and cheap but **not load-bearing**. The overwhelming majority of fields were already being handled correctly as edges. The fallthrough catches a small tail of edge cases, not the thousands of entries I initially thought were being dropped.

Phase 5 has been re-run against `starwars-dev` and the KG is now at **166,424 nodes / 595,159 edges / 484 labels**. Build completed cleanly.

## What's deferred (pending a follow-up session)

- **Layer A (self-correcting hints)** on `count_nodes_by_property`, `count_nodes_by_properties`, `count_property_for_related_entities`.
- **Layer B (sharpened descriptions)** on the same four tools plus `group_entities_by_connection`.
- **Deletion of Pages-side duplicates.** Per Patrick's north star, Pages-side tools should be retired as KG reaches coverage parity. `list_relationship_labels` is a clean deletion candidate (strict subset of `describe_relationship_labels`). `sample_property_values`, `get_page_property`, and similar are deletable once the KG-side routing is tight enough that the agent never needs to fall back to raw.

## What we tried first and why it was wrong

The original proposal in this doc (now corrected) was to implement **property/edge duality** — always store the literal infobox text as a property *in addition to* emitting the edge, so that `count_nodes_by_property` would return the truth. I dressed this up with a "properties for aggregation, edges for traversal" framing that Patrick immediately called out: aggregation works perfectly on edges via `group_entities_by_connection`, and I was inventing a data-shape fix for what was actually a tool-routing bug.

Second pitfall: the coverage table I used to justify the duality proposal only read `kg.nodes.properties` and never checked `kg.edges`. When I finally did the cross-check (after the Phase 5 rebuild), every "missing" field turned out to already exist as edges, often at higher counts than the raw source. The entire "data loss" framing was an artifact of me looking at the wrong collection.

Lessons:

1. **When the KG is the source of truth, check both collections before concluding anything is missing.** `kg.nodes.properties` is only one half of the model; `kg.edges` carries link-bearing facts and normalizes them into typed relationships. A low property-count for a field name is normal, not alarming.
2. **Don't propose data-layer fixes for tool-layer bugs.** If the data is correct and complete but the agent is asking the wrong tool for it, the fix is in the tool descriptions or the tool routing, not in the storage shape.
3. **Edge labels are normalized, field names are not.** "Species" on a Character page corresponds to the edge label `species`, "Place of origin" on a Food page corresponds to `originates_from`, "commanders1" on a Battle page corresponds to `commanded_by`. An agent (or a tool) trying to route between them needs to know the mapping. The routing layer should either (a) know the mapping, or (b) derive it from the label normalization function (`NormaliseLabel` in InfoboxGraphService) + the InfoboxDefinitionRegistry relationship labels.
4. **The `kg.labels` registry already exists** ([ADR-003 / Design-007 / project_kg_labels_registry memory](project_kg_labels_registry)) as a materialized view of all edge labels with usage counts, from/to types, and descriptions. The self-correcting hint can query this directly instead of running an ad-hoc aggregation.

## References

- The Phase 5 run that produced the corrected coverage check: trace `755b3f0` on `admin-wvswbkgy`, 2026-04-11, 166,424 nodes / 595,159 edges.
- The agent's failing tool call that started the investigation: `search_pages_by_property(infoboxType="Food", label="Origin", value="Corellia") → []`.
- [InfoboxGraphService.cs](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) — the KG builder.
- [KGAnalyticsToolkit.cs](../../src/StarWarsData.Services/AI/Toolkits/KGAnalyticsToolkit.cs) — where the routing fix will land.
- [Design 012: AI Agent Tool-Call Efficiency](012-ai-agent-tool-call-efficiency.md) — earlier session on the 71-call Endor question; the routing discipline proposed there is directly related to the Layer B fix here.
- ADR-002: [AI Agent Toolkits on Microsoft Agent Framework](../adr/002-ai-agent-toolkits.md).
