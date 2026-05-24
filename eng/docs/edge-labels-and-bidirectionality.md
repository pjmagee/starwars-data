# Edge labels and bidirectionality in the KG

**Status:** Research note (no code change)
**Date:** 2026-04-29
**Author:** Patrick Magee + Claude
**Scope:** Grounding doc for the [Design-026](../../specs/026-holocron-orchestration-pattern/spec.md) conversation. Captures the *current* end-to-end pipeline for how edge labels come into being, what bidirectionality support exists today, and how Design-026's tool-using Holocron agent intersects with that pipeline. Closes with a gaps section enumerating the changes that would be needed for fluid two-way reading.

**Related:**
- [ADR-003 KG query architecture](../adr/003-kg-query-architecture.md)
- [ADR-006 Long-running AI workflows](../adr/006-long-running-ai-workflow-pipelines.md)
- [Design-002 Edge quality](../../specs/002-edge-quality/spec.md)
- [Design-007 KG bidirectional edges view](../../specs/007-kg-bidirectional-edges-view/spec.md)
- [Design-035 Per-type builders](../../specs/035-kg-per-type-builders/spec.md)
- [Design-008 Hierarchy helpers](../../specs/008-kg-hierarchy-helpers/spec.md)
- [Design-013 Property/edge duality](../../specs/013-kg-property-edge-duality/spec.md)
- [Design-018 KG enrichments architecture](../../specs/018-kg-enrichments-architecture/spec.md)
- [Design-019 Enrichment UI provenance](../../specs/019-kg-enrichment-ui-provenance/spec.md)
- [Design-020 Holocron async pipeline](../../specs/020-holocron-async-pipeline/spec.md)
- [Design-021 Edge bound provenance](../../specs/021-edge-bound-provenance/spec.md)
- [Design-024 Typed NodeBuilders](../../specs/024-typed-node-builders/spec.md)
- [Design-025 Tool-using Holocron agent](../../specs/025-holocron-tool-using-agent/spec.md)
- [Design-026 Holocron orchestration pattern](../../specs/026-holocron-orchestration-pattern/spec.md)

## TL;DR

1. Edge labels are not "discovered" — they are produced by a **single hand-curated dictionary**, `FieldSemantics.Relationships`, mapping infobox field labels to a `LabelDefinition { Label, Reverse, ExpectedTargetTypes, … }`. The generic `NodeBuilderBase` reads that dict for every infobox row that emits an edge.
2. Per-type `NodeBuilder.OnFinalize` overrides (Design-024 Phases A–C) **rewrite** the canonical label *after* generic emission for source × target-type-specific cases (Character→TitleOrPosition becoming `has_role`, Sector field-alias collapse, etc.). Net-new labels enter the system here, by C# code change.
3. Bidirectionality today is **denormalised on each edge** as `reverseLabel` plus a `kg.edges.bidir` *view* that re-emits each edge with `from`/`to` flipped and `label = reverseLabel`. There is **no `inverseLabel` field on `kg.labels`** beyond the canonical `reverse` snake_case identifier — no human-readable "is master of" / "was apprentice to" sentence templates exist anywhere.
4. Design-026's tool-using agent introduces three *write* tools (`propose_edge`, `propose_property`, `suggest_new_label`) that stage proposals into per-batch state and flush via the existing `HolocronApplyExecutor` to `kg.enrichments` / `kg.edge_enrichments`. **The agent cannot mint canonical labels** — `find_canonical_label` is the only path to a label, and `suggest_new_label` writes to a (not-yet-implemented) `kg.label_suggestions` review queue. `inverseLabel` / reverse-reading hints are **not** part of any proposed Design-026 tool surface.
5. Fluid two-way reading would require: (a) a forward/reverse human-readable string pair on `LabelDefinition` (or `kg.labels`), (b) a UI-side display mapper that consults that pair when rendering inbound edges, and (c) optionally a Holocron `suggest_inverse_label` tool wired to the same review queue as `suggest_new_label`. None of these exist today.

---

## 1. How edge labels are created today

### 1.1 The chain in one paragraph

`raw.pages.infobox.Data[].Label` is the *infobox field name*. `InfoboxGraphService` iterates every page with an infobox, dispatches it to a per-type `INodeBuilder` ([InfoboxGraphService.RegisterAllBuilders registers ~50 builders](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs)). The generic loop in `NodeBuilderBase.Build` looks each field up in the per-template `InfoboxDefinition` (intersection of `TemplateFields` × `FieldSemantics`), and if it's a relationship, copies `LabelDefinition.Label` onto the emitted `RelationshipEdge.Label` ([NodeBuilderBase.cs:223](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs)). For fields that have no `LabelDefinition` but do have links, the label is synthesised by `NormaliseLabel` (snake_case-ifies the field name verbatim — same file, line 520). After all builders run, the coordinator's post-processing pass enriches `ToType`/`ToRealm`, denormalises `reverseLabel` from `FieldSemantics`, and drops noise edges before insert.

### 1.2 There is no separate "synonym table"

Despite [Design-025](../../specs/025-holocron-tool-using-agent/spec.md)'s framing, **today there is no `LabelSynonymTable` class anywhere in the codebase**. The conceptual synonym table exists *as a side-effect* of the multi-key-single-value shape of `FieldSemantics.Relationships` — many distinct field-label keys all map to the same canonical `Label` string:

```csharp
// FieldSemantics.cs:204..216
["Homeworld"]          = new("homeworld",       "homeworld_of",  ...),
["Homeworld(s)"]       = new("homeworld",       "homeworld_of",  ...),
["Place of origin"]    = new("originates_from", "origin_of",     ...),
["Origin"]             = new("originates_from", "origin_of",     ...),
["Place(s) of origin"] = new("originates_from", "origin_of",     ...),
```

So the "synonym set" for canonical label `homeworld` is *implicit* — it's the set of `FieldSemantics.Relationships` keys whose `Label` is `"homeworld"`. There is no inverse lookup ("what canonical label do these prose verbs map to"). The closest thing in the wider system is `RelationshipAnalystToolkit.FindSimilarLabel` ([RelationshipAnalystToolkit.cs:195](../../src/StarWarsData.Services/AI/Toolkits/RelationshipAnalystToolkit.cs)), which does a runtime Levenshtein-style scan over `kg.labels` for similarity ranking — used by the Phase 6 LLM batch path to discourage label sprawl, *not* by the Phase 1 infobox extractor.

This matters for [Design-025](../../specs/025-holocron-tool-using-agent/spec.md): when it says "a synonym table maps natural-language predicates to canonical labels, scoped by source × target type" and shows `("worked as", Character, TitleOrPosition) → has_role`, that table **does not exist yet**. It will need to be authored from scratch, possibly seeded from the `FieldSemantics.Relationships` keys + per-type `OnFinalize` rules.

### 1.3 The `LabelDefinition` record

Every entry in `FieldSemantics.Relationships` is a `LabelDefinition` ([LabelDefinition.cs](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/LabelDefinition.cs)):

```csharp
public sealed record LabelDefinition(
    string Label,                // canonical snake_case ("apprentice_of")
    string Reverse,              // canonical reverse snake_case ("master_of")
    string Description,          // human-readable, surfaced in tool discovery
    string[] ExpectedTargetTypes,// e.g. ["Character", "Person"]
    string Category,             // family / mentorship / military / publication / …
    double Weight = 1.0
);
```

Two things stand out:

- `Reverse` is **a string, not a back-pointer to another `LabelDefinition`**. There is no constraint that `Reverse` of "apprentice_of" exists as a *forward* canonical label elsewhere. (In practice it does — every reverse pair is also a forward key — but that's an authorial convention, not enforced by code.)
- `ExpectedTargetTypes` drives downstream filtering (e.g. `IsPersonRelationshipLabel` and the `kg.edges` qualifier-noise drop in `InfoboxGraphService.cs:253–264`) but is **not** consulted by the Holocron pre-flight today. [Design-025](../../specs/025-holocron-tool-using-agent/spec.md) wants to pull it in via `propose_edge`'s tool-side validation.

### 1.4 What `NodeBuilderBase` actually emits

In [NodeBuilderBase.cs:213–283](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs):

```csharp
var edgeLabel = labelDef?.Label ?? NormaliseLabel(label);
…
edges.Add(new RelationshipEdge {
    …,
    Label = edgeLabel,
    Evidence = $"Infobox field '{label}'",
    Meta = new EdgeMeta {
        Qualifier = pl.Qualifier,
        SourceFieldLabel = label,                // Phase A — Design-024
        BoundsSource = hasInfoboxYear ? EdgeBoundsSource.Infobox : EdgeBoundsSource.Unknown,
    },
});
```

So `Meta.SourceFieldLabel` preserves the *original* field label ("Affiliation(s)", "commanders1", "Sector capital") as the audit trace; `Label` is the canonical form. Per-type `OnFinalize` reads `Meta.SourceFieldLabel` to decide whether to rewrite `Label`.

### 1.5 Per-type relabelling — the only place "new" labels appear

[Design-024](../../specs/024-typed-node-builders/spec.md) Phase A added a per-type post-processing hook. The cleanest example is `CharacterNodeBuilder` ([CharacterNodeBuilder.cs:40–78](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/Types/CharacterNodeBuilder.cs)):

```csharp
protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
{
    foreach (var edge in edges)
    {
        if (edge.Meta?.SourceFieldLabel is not "Affiliation" and not "Affiliation(s)") continue;
        if (edge.Label != "affiliated_with") continue;
        var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
        switch (targetType)
        {
            case KgNodeTypes.TitleOrPosition: edge.Label = "has_role"; break;
            case KgNodeTypes.Family:          edge.Label = "member_of_family"; break;
            case KgNodeTypes.Religion:        edge.Label = "member_of"; break;
            case KgNodeTypes.Species:         edge.Label = "has_ethnicity"; break;
            case KgNodeTypes.City:            edge.Label = "from_city"; break;
            case KgNodeTypes.Company:         edge.Label = "works_for"; break;
            case KgNodeTypes.MilitaryUnit:
            case KgNodeTypes.Fleet:           edge.Label = "serves_in"; break;
        }
    }
}
```

The rewrites are **closed-form, hardcoded switches**. The labels they introduce (`has_role`, `member_of_family`, `has_ethnicity`, `from_city`, `serves_in`) are pre-registered in `FieldSemantics.Relationships` under synthetic `__has_role` / `__member_of_family` / etc. keys ([FieldSemantics.cs:493–499](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs)). Those keys never match an infobox field — they exist purely so the canonical label has metadata (reverse, description, target types) when `kg.labels` is materialised.

Other Phase B/C overrides:

- `BattleNodeBuilder.OnFinalize` → `ConflictSideEncoder.StampSideIndex` ([Types/BattleNodeBuilder.cs:16](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/Types/BattleNodeBuilder.cs)). Doesn't change labels — only stamps `Meta.SideIndex`.
- `OrganizationNodeBuilder.OnFinalize` ([Types/OrganizationNodeBuilder.cs:21–35](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/Types/OrganizationNodeBuilder.cs)) — drops `led_by → Organization` edges as parser noise. Filter-only, no relabel.
- `SectorNodeBuilder` (Phase B) — collapses fragmented sub-conflict labels into `has_conflict` (cited in `feature/holocron-skeleton` history).

**Net:** new edge labels enter the system in exactly two places: (a) a new entry in `FieldSemantics.Relationships`, or (b) a hardcoded relabel inside a `NodeBuilder.OnFinalize`. Both are C# changes. No data-driven discovery path exists.

### 1.6 Coordinator post-processing

After all builders return, [`InfoboxGraphService.BuildGraphAsync`](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs) does:

1. **Type/realm enrichment** — stamp `ToType`/`ToRealm` from the node maps.
2. **`reverseLabel` denorm** — look the edge's `Label` up in a `FieldSemantics.Relationships`-derived map and copy `Reverse` onto the edge. Edges whose label has no registered reverse leave `ReverseLabel` null — those are deliberately dropped from the bidir view's reverse branch.
3. **Noise filter** — drop unresolved (`ToId == 0`), Year/Era targets, qualifier-as-target rows.
4. **Lifecycle bound derivation** + `BoundsSource` stamping ([Design-021](../../specs/021-edge-bound-provenance/spec.md)).
5. **Dedup** by `(FromId, ToId, Label)` — matches the unique constraint `ix_fromId_toId_label`.
6. **Lineage closure precompute** for `HierarchyRegistry.Lineages` ([Design-008](../../specs/008-kg-hierarchy-helpers/spec.md)).
7. **Indexes** + `BuildLabelRegistryAsync` + `EnsureBidirectionalEdgesViewAsync`.

### 1.7 The `kg.labels` registry

`InfoboxGraphService.BuildLabelRegistryAsync` builds it as follows:

1. Group by `$label` over `kg.edges`, aggregating `usageCount`, `fromTypes`, `toTypes`.
2. For each observed label, look up its `LabelDefinition` in `FieldSemantics.Relationships` (deduped by canonical Label); copy `Reverse` + `Description`.
3. For any `LabelDefinition` not seen in any edge, insert a seed-only row with usageCount 0.
4. Persist as `kg.labels` documents, indexed on `usageCount`, `fromTypes`, `toTypes`.

The `RelationshipLabel` document shape ([RelationshipLabel.cs](../../src/StarWarsData.Models/KnowledgeGraph/RelationshipLabel.cs)):

```csharp
public class RelationshipLabel {
    [BsonId] public string Label { get; set; }    // canonical
    [BsonElement("reverse")]     public string Reverse { get; set; }
    [BsonElement("description")] public string Description { get; set; }
    [BsonElement("fromTypes")]   public List<string> FromTypes { get; set; }
    [BsonElement("toTypes")]     public List<string> ToTypes { get; set; }
    [BsonElement("usageCount")]  public int UsageCount { get; set; }
    [BsonElement("createdAt")]   public DateTime CreatedAt { get; set; }
}
```

**There is no `inverseLabel` field beyond `reverse`, no `forwardReading`, no `reverseReading`, no human-readable display strings.** `Reverse` is the snake_case canonical form ("master_of"), not a sentence template.

### 1.8 Schema validators

`kg.edges` and `kg.nodes` carry `$jsonSchema` validators in moderate/warn mode ([validators.js:12–51](../../src/StarWarsData.MongoDbMigrations/lib/validators.js)):

```javascript
label:        { bsonType: "string", minLength: 1 },
reverseLabel: { bsonType: ["string", "null"], description: "reverse form from FieldSemantics" },
```

The validator does **not** restrict `label` to a closed set — any non-empty string is allowed. Constraint enforcement on canonical-vocabulary lives in C# (`HolocronAgent._knownLabels`, `HolocronConsolidatorExecutor._knownLabels`), not in Mongo.

The enrichment validators (`nodeEnrichmentValidator`, `edgeEnrichmentValidator`) likewise accept any non-empty `label` string but DO restrict `operation` to `["Add", "Augment", "FillGap", "Annotate"]` and `status` to `["Active", "Superseded", "Stale", "Rejected"]`.

---

## 2. Bidirectionality today

### 2.1 What `kg.edges.bidir` actually does

Defined twice — once in C# at [InfoboxGraphService.EnsureBidirectionalEdgesViewAsync](../../src/StarWarsData.Services/KnowledgeGraph/InfoboxGraphService.cs), once in [migration 0007](../../src/StarWarsData.MongoDbMigrations/migrations/0007-create-kg-bidir-view.js). The C# rebuild runs at the end of every Phase 5 (`drop` then `createView`); the migration is the bootstrap path.

The view is built as `$unionWith` of two pipelines:

- **Forward branch** — every edge as-stored, plus `direction: "forward"`.
- **Reverse branch** — only edges with non-empty `reverseLabel`, projected with `fromId`/`toId`/`fromName`/`toName`/`fromType`/`toType`/`fromRealm`/`toRealm` swapped, and **`label` replaced by `$reverseLabel`**, plus `direction: "reverse"`.

So when `$graphLookup` walks the view:

- An `apprentice_of` forward edge `Anakin → Obi-Wan` shows up in the forward branch as `(Anakin, Obi-Wan, apprentice_of, forward)`.
- The same physical edge shows up in the reverse branch as `(Obi-Wan, Anakin, master_of, reverse)`.

**The reverse branch DOES relabel** — it's the whole point. The view answers "starting from Obi-Wan, who are his apprentices?" with rows whose `label = master_of` even though the underlying physical edge is `apprentice_of`. This is what lets the planned `QueryGraphAsync` rewrite ([Design-007](../../specs/007-kg-bidirectional-edges-view/spec.md)) collapse the manual hop-by-hop BFS into a single `$graphLookup`.

### 2.2 What the view does NOT do

- It does **not** carry `meta` in the reverse branch — the `$project` stage in the reverse pipeline omits it (cited explicitly as "intentional for now" in [Design-007](../../specs/007-kg-bidirectional-edges-view/spec.md)).
- It does **not** invent display strings — `label = master_of` in the reverse branch is still a snake_case identifier, not "is the master of".
- It does **not** persist — every Phase 5 drops and recreates the view definition. There is no materialised `kg.edges.bidir` collection.
- The view drops edges with no registered reverse from its reverse branch (~2.2% of edges per Design-007 stats).

### 2.3 The C# BFS path also relabels — twice

The current `QueryGraphAsync` ([KnowledgeGraphQueryService.cs:976–1114](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs), pre-Design-007 rewrite) builds *two* lookups from `FieldSemantics`:

```csharp
var forwardToReverse = FieldSemantics.Relationships.Values
    .DistinctBy(d => d.Label)
    .ToDictionary(d => d.Label, d => d.Reverse, OrdinalIgnoreCase);
var reverseToForward = FieldSemantics.Relationships.Values
    .DistinctBy(d => d.Reverse)
    .Where(d => !string.IsNullOrEmpty(d.Reverse))
    .ToDictionary(d => d.Reverse, d => d.Label, OrdinalIgnoreCase);
```

Inbound edges (where `toId = root`) are flipped and relabelled via `forwardToReverse` so callers see them in root-anchored form. Client-supplied label filters in reverse form are translated back to forward via `reverseToForward` so the underlying `kg.edges` query uses the stored label.

`GetLabelsForEntityAsync` and `GetEntityEdgesAsync` do the same — every consumer of "from this node's perspective" data builds the same two dictionaries from `FieldSemantics.Relationships`. **There is no single helper today.**

### 2.4 UI display: snake_case → Title Case is the entire transform

The UI rendering is intentionally minimal. `GraphViewer.razor` ([GraphViewer.razor:523–524](../../src/StarWarsData.Frontend/Components/Shared/GraphViewer.razor)):

```csharp
static string FormatLabel(string label) =>
    System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.Replace("_", " "));
```

So `apprentice_of` becomes "Apprentice Of" and `master_of` becomes "Master Of". When the viewer shows an edge in either direction, the relabelling to the appropriate canonical form has *already happened* — at the C# layer (manual BFS) or the view layer (`$graphLookup` over `kg.edges.bidir`). The UI never gets `apprentice_of` and has to flip it to "Master Of"; it just title-cases whatever label it received.

### 2.5 What the AI sees

`GraphRAGToolkit.GetEntityRelationships` returns edges already relabelled and direction-flipped through the same `FieldSemantics`-derived `reverseLookup` ([KnowledgeGraphQueryService.cs:382](../../src/StarWarsData.Services/KnowledgeGraph/KnowledgeGraphQueryService.cs)). The agent receives "Anakin commanded 501st Legion" (forward) and "Battle of Geonosis happened during Clone Wars" (which started life as `part_of_conflict` and got reverse-mapped to `includes_battle` for the Battle's perspective) — both as snake_case labels from a single, root-anchored perspective.

`RelationshipAnalystToolkit.GetExistingLabels` ([RelationshipAnalystToolkit.cs:179–186](../../src/StarWarsData.Services/AI/Toolkits/RelationshipAnalystToolkit.cs)) does return `{ Label, Reverse }` pairs in its DTO (`RelationshipLabelDto`), so the LLM in the Phase 6 batch path *can* see both forms, but consumes them as canonical strings, not reading templates. There is no `naturalLanguageReading` field anywhere.

### 2.6 Summary of the bidirectionality state

| Concern | Current state |
|---|---|
| Single-edge two-row view | Yes (`kg.edges.bidir`, [Design-007](../../specs/007-kg-bidirectional-edges-view/spec.md)). |
| Reverse branch relabels via `reverseLabel` | Yes — that is the whole point. |
| `inverseLabel` / `reverse` field on `kg.labels` | Only as `RelationshipLabel.Reverse` — copied verbatim from `FieldSemantics`, snake_case, no English template. |
| `EdgeMeta.boundsSource` direction-aware | No — `Meta` isn't projected into the reverse view branch. |
| Reverse-reading sentence templates ("X is the master of Y") | None anywhere. |
| Per-type label disambiguation across direction | Partially — through Phase B/C `OnFinalize` rewriting on the *forward* side only (Character→TitleOrPosition becomes `has_role`; the reverse `held_by` is registered alongside but never gets per-type re-disambiguation). |
| AskAI / GraphRAG natural-language phrasing | Done by the LLM at response time — toolkits hand it snake_case + a description string and let the LLM phrase the prose. |

---

## 3. How new labels / edges / properties enter today (pre-Design-026)

### 3.1 New canonical edge labels

Two paths, **both code changes**:

1. Add a row to `FieldSemantics.Relationships`. Either keyed on a real infobox field name (the wiki uses that field) or on a synthetic `"__some_label"` key (the label is purely produced by per-type relabelling — see `__has_role`, `__has_language`, `__assigned_to` in [FieldSemantics.cs:494–512](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs)).
2. Add a per-type `OnFinalize` switch case that *uses* an already-registered canonical label to rewrite a more generic one — e.g. `CharacterNodeBuilder` rewriting `affiliated_with` to `has_role`.

`kg.labels` is **observed-only** — it inserts rows for any label that appears on at least one edge plus seed-only rows for any registered `LabelDefinition` not yet observed. There is **no** "promote a frequent ad-hoc label to canonical" workflow today; `kg.labels` is a downstream materialisation, not a source of truth.

There is also one historical LLM-driven path: `RelationshipAnalystToolkit.UpsertLabel` ([RelationshipAnalystToolkit.cs:389](../../src/StarWarsData.Services/AI/Toolkits/RelationshipAnalystToolkit.cs)) does a `SetOnInsert(reverse, fromTypes, toTypes)` upsert into `kg.labels` when the LLM batch path stores a new edge. This path is **not used by Holocron**; Phase 1 + Phase 6's deterministic builders do not call it. It survives as the only place in the codebase where data — not code — extends the canonical label list, and it can introduce labels that have **no** entry in `FieldSemantics.Relationships`.

### 3.2 New edge types

Edge types are not a separate concept — every edge has a `Label`, period. "New edge type" means "new canonical label". See above.

### 3.3 New node properties

Identical mechanism. Properties are gated by `FieldSemantics.Properties` (a `HashSet<string>` of field labels — [FieldSemantics.cs:23–196](../../src/StarWarsData.Services/KnowledgeGraph/Definitions/FieldSemantics.cs)) plus the per-template intersection in `InfoboxDefinitionRegistry.ForTemplate`. To add a new property:

1. Add the field label to `FieldSemantics.Properties`.
2. Optionally, ensure the field label appears in `TemplateFields.g.cs` (auto-generated by inventorying MongoDB).
3. Optionally, add per-type post-processing in a `NodeBuilder.OnFinalize` if the value needs reshaping.

The fallthrough at [NodeBuilderBase.cs:64–70](../../src/StarWarsData.Services/KnowledgeGraph/NodeBuilders/NodeBuilderBase.cs) preserves any unclassified scalar field as a property under its raw label — so even unknown fields land in `kg.nodes.properties` rather than being silently dropped ([Design-013](../../specs/013-kg-property-edge-duality/spec.md) property/edge duality).

### 3.4 New node types

`KgNodeTypes` is a `string`-constants class. New types require:

1. A constant in `KgNodeTypes`.
2. A new `INodeBuilder` under `NodeBuilders/Types/`.
3. A line in `InfoboxGraphService.RegisterAllBuilders`.

### 3.5 Validation enforcement

For Phase 1 deterministic data, the only "validation" is the schema validators (which permit any non-empty string label). The dedup unique index `ix_fromId_toId_label` enforces uniqueness per triple.

For Phase 2 Holocron data, both `HolocronAgent.IsAddEdgeValid` ([HolocronAgent.cs:1577–1586](../../src/StarWarsData.Services/AI/Agents/HolocronAgent.cs)) and `HolocronConsolidatorExecutor.IsAddEdgeValid` ([HolocronConsolidatorExecutor.cs:512–518](../../src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronConsolidatorExecutor.cs)) reject any label not in `_knownLabels = InfoboxDefinitionRegistry.AllLabelDefinitions().Select(d => d.Label).ToHashSet()`. These two copies must be kept in sync — drift here cost a full Anakin run when [Design-021](../../specs/021-edge-bound-provenance/spec.md) missed the consolidator copy.

This is the closed-vocabulary contract today. Holocron cannot mint a label.

---

## 4. How Design-026's tool-using agent changes the picture

[Design-025](../../specs/025-holocron-tool-using-agent/spec.md) commits to the architecture; [Design-026](../../specs/026-holocron-orchestration-pattern/spec.md) commits to the orchestration shape (concurrent fan-out of `AIAgentHostExecutor` instances over per-batch chunk sets). The seven tools come from Design-025.

### 4.1 Read tools (no graph mutation)

| Tool | Backing store | Notes |
|---|---|---|
| `resolve_entity(text)` | `kg.nodes` (name + Titles alias) | Same path `KnowledgeGraphQueryService.SearchNodesAsync` already uses. |
| `find_canonical_label(predicate_prose, source_type, target_type)` | A new synonym table (Design-025 §"Add"). Does not exist yet. | Returns canonical label or `null`. |
| `check_existing_edges(from_id, to_id)` | `kg.edges` (both directions). | Powers the agent's choice between Annotate and Add. |
| `get_template_schema(node_type)` | `InfoboxDefinitionRegistry.ForTemplate(node_type)`. | Mirrors the per-template field whitelist. |

`find_canonical_label` is the only path by which the agent can name a label. `check_existing_edges` is direction-aware (reads both `from→to` and `to→from`); how it presents inbound edges (verbatim or reverse-mapped) is **not** specified in Design-025.

### 4.2 Write tools (stage proposals; do not write `kg.edges` / `kg.nodes`)

Per Design-025 §"What we keep, what we throw away" + Design-026 §"Tool surface lives on the agent":

| Tool | Stages into | Eventual destination | Can mint canonical labels? |
|---|---|---|---|
| `propose_edge` | Per-batch staging store keyed `(batchIndex, proposalId)` (Design-026 §"Tool surface lives on the agent"). | `kg.edge_enrichments` (operation = Add / Annotate / FillGap) via the absorbed-into-Apply consolidator. | **No.** Tool-side validation rejects any label not in canonical registry, mirroring current `IsAddEdgeValid`. |
| `propose_property` | Same per-batch store. | `kg.enrichments` (operation = Add / Augment) via Apply. | N/A — properties don't have labels in the same sense. Validates `field_path` against `InfoboxDefinitionRegistry.ForTemplate(target_type).Properties` and rejects values that resolve to a node (anti-Aliases-stuffing rule). |
| `suggest_new_label` | A new `kg.label_suggestions` collection (Design-025 §"Vocabulary-growth tool"). **Does not exist today.** | Human-review queue; promoting a suggestion to canonical is a code change to `FieldSemantics.Relationships`. | **No** — only logs the suggestion. Promotion is out-of-band. |

So Design-025 explicitly makes label-vocabulary growth a first-class signal but does **not** auto-promote. The Holocron run that emits `suggest_new_label("X briefly fought alongside Y at Z", Character, Battle, …)` does not produce a usable canonical label until a human reviews the aggregated evidence in `kg.label_suggestions` and edits `FieldSemantics.Relationships`. [Design-019](../../specs/019-kg-enrichment-ui-provenance/spec.md)'s per-node enrichment UI is the closest existing review surface — extending it for `kg.label_suggestions` is implied but not fleshed out.

### 4.3 What's NOT in Design-025 / Design-026

- No `inverse_label` field on the proposal payloads. `propose_edge` takes one `label` argument; bidirectionality is implicitly inherited from `FieldSemantics.Relationships[label].Reverse` once the edge is staged.
- No tool for proposing a *reverse-reading sentence* (e.g. "is the master of"). Holocron's outputs remain canonical snake_case identifiers; phrasing happens at LLM-output time elsewhere.
- No tool for proposing a new node *type*. Type taxonomy is locked (`KgNodeTypes` constants + per-type builders). Nothing in Design-025 changes that.
- No interaction with `kg.labels` or `kg.edges.bidir` from the tool surface — those remain Phase 1 derivations, materialised after the next infobox rebuild.

### 4.4 Apply path is unchanged in shape

`HolocronApplyExecutor` ([Workflows/HolocronApplyExecutor.cs:80–98](../../src/StarWarsData.Services/AI/Agents/Holocron/Workflows/HolocronApplyExecutor.cs)) still writes to `kg.enrichments` / `kg.edge_enrichments` / `kg.events` / `kg.node_processed_chunks`. Design-026 adds cross-batch dedup as a fan-in barrier but does not change the on-disk schema. The schema validators ([validators.js:153–180](../../src/StarWarsData.MongoDbMigrations/lib/validators.js)) remain.

---

## 5. Gaps for fluid bidirectional reading

Synthesising sections 1–4: the *graph* has bidirectionality covered (the `reverseLabel` denorm + `kg.edges.bidir` view make either-direction traversal tractable). The *reading layer* — making "Anakin apprentice_of Obi-Wan" come out as "Obi-Wan was Anakin's master" without the LLM doing the phrasing — does not exist. Concretely, the deltas would be:

1. **Human-reading strings on `LabelDefinition`.** Add `string ForwardReading` and `string ReverseReading` (e.g. `apprentice_of` → "is apprentice of" / `master_of` → "is master of") to `LabelDefinition`. Backfill all ~200 existing entries.
2. **Surface those strings on `kg.labels`.** Add `forwardReading` / `reverseReading` to the `RelationshipLabel` document and `BuildLabelRegistryAsync`'s seed copy. Update the schema validators in [validators.js:53](../../src/StarWarsData.MongoDbMigrations/lib/validators.js).
3. **A single shared formatter helper.** Today every consumer (`KnowledgeGraphQueryService`, `RelationshipAnalystToolkit`, `GraphRAGToolkit`, `GraphViewer.razor`) builds its own `forwardToReverse` / `reverseToForward` map from `FieldSemantics`. A `LabelFormatter` service that maps `(label, direction) → reading` would consolidate this and become the single read-side authority.
4. **UI changes.** `GraphViewer.FormatLabel` and the per-node panel should consult that formatter so an inbound edge reads as "Obi-Wan **is master of** Anakin" rather than "Obi-Wan apprentice of Anakin (reverse)" or "Master Of". [Design-019](../../specs/019-kg-enrichment-ui-provenance/spec.md)'s per-node panel and the Pages-side relationship list are the highest-impact spots.
5. **Holocron tool surface (Design-025/026).** Two possible additions, neither currently planned:
   - `suggest_inverse_label` — when the agent invents a forward label via `suggest_new_label`, it can also propose the inverse reading; a single review step approves both directions atomically.
   - `propose_edge` could optionally accept a `reading_hint` so the LLM's phrasing intuition reaches the review queue instead of being discarded. Cost: every staged edge carries an extra free-text field; reviewers can keep or drop.
6. **Per-type direction-specific reading.** Today's per-type relabelling ([Design-024](../../specs/024-typed-node-builders/spec.md) Phase B) only acts on the forward side: `Character.affiliated_with → Religion` becomes `member_of`, but the reverse side just inherits the canonical `member_of`'s `has_member`. If the readings are introduced, they should ideally be per-source-type as well — "X is a member of the Jedi Order" reads better than "X has membership in the Jedi Order"; the inverse reading "the Jedi Order has X as a member" reads better than "the Jedi Order is member-of'd by X". This is the most authorial-effort-heavy delta, and it scales with the per-type rewrite catalogue.
7. **Reverse-branch `meta` projection on `kg.edges.bidir`.** Currently dropped. If readings are stored in `meta.reading.forward` / `meta.reading.reverse` (as opposed to on `kg.labels`), the bidir view must learn to project them in the reverse branch with the same "use the appropriate direction" logic that `reverseLabel` already gets.

None of these gaps blocks Design-026. They are an orthogonal evolution of the read layer — the *write* path already has everything it needs to enable them later.

---

## Notes on what's light

- The "reading" / natural-language angle gets very thin coverage in the existing docs. [Design-007](../../specs/007-kg-bidirectional-edges-view/spec.md) (bidir view), [Design-013](../../specs/013-kg-property-edge-duality/spec.md) (property/edge duality), and [Design-024](../../specs/024-typed-node-builders/spec.md) (typed builders) all work in canonical-string-space; nobody has written a design for human-readable bidirectional rendering. If section 5 above grows into a Design-027 it will be greenfield.
- The "synonym table" [Design-025](../../specs/025-holocron-tool-using-agent/spec.md) references is conceptually the keys of `FieldSemantics.Relationships` + per-type relabel rules but is **not** a single materialised artefact today. Building it for the `find_canonical_label` tool will require either (a) authoring a fresh `LabelSynonyms.cs` or (b) deriving it programmatically — both options are open in Design-025 §"Open questions" §2.
- The `RelationshipAnalystToolkit.UpsertLabel` LLM-driven path is the only existing in-product way to add a `kg.labels` entry without a code change. It's used by the Phase 6 batch path; it's not used by Holocron. If a future Holocron tool ever auto-promoted suggestions, this is the historical precedent — and a cautionary tale, since labels added this way have no `FieldSemantics` entry and therefore no description, target types, or reverse pair beyond what the LLM happened to write at insert time.
