# Design: Knowledge Graph — Per-Type Node Builders

**Status:** Proposal
**Date:** 2026-04-06
**Companion docs:** [002-ai-agent-toolkits.md](../adr/002-ai-agent-toolkits.md)

## Problem

`InfoboxGraphService` is a single monolithic class (~500 LOC) that processes every infobox template type through one generic extraction loop. The loop classifies each field as temporal, scalar property, or relationship edge using `FieldSemantics` and `TemplateFields` — but applies the same logic regardless of node type.

This breaks down when different node types need **type-specific extraction behaviour**:

- **TradeRoute** nodes need ordered waypoint sequences preserved as `pageId` lists (the generic loop loses ordering when creating edges).
- **System** nodes have orbital body relationships (`orbited_by`) where the infobox distinguishes planets, moons, asteroids, and space stations — but the generic loop collapses them into edges without preserving the hierarchical structure.
- **Character** nodes have complex lifecycle chains (born → trained → fought → died) that could benefit from type-specific temporal facet assembly.
- **Government** nodes have institutional lifecycle transitions (established → reorganized → fragmented → restored → dissolved) that require careful facet ordering — currently handled generically by `AssignFacetOrder`.
- **Battle** nodes have belligerent/commander relationships that are side-specific (attacker vs defender) — the generic loop loses the grouping.

Each time we need type-specific behaviour, we add `if (type == KgNodeTypes.X)` branches inside the generic loop. This is unsustainable — the service grows, the branches interact, and the generic path becomes increasingly polluted with special cases.

## Current special cases (as of 2026-04-06)

| Type | Special case | Location |
| --- | --- | --- |
| TradeRoute | Store ordered `{field}Ids` properties alongside edges | `InfoboxGraphService.cs` — `type == KgNodeTypes.TradeRoute` block |

This list will grow as more type-specific requirements emerge (e.g. the temporal galaxy map work in `006-galaxy-map-timeline-mode.md` which needs destruction events on CelestialBody nodes).

## Proposal

Replace the monolithic extraction loop with a **per-type builder pattern**:

```
INodeBuilder
  ├── DefaultNodeBuilder        (current generic logic — fallback for types without a custom builder)
  ├── TradeRouteNodeBuilder     (ordered waypoint sequences, junction resolution)
  ├── SystemNodeBuilder         (orbital hierarchy, star classification)
  ├── GovernmentNodeBuilder     (institutional lifecycle assembly)
  ├── BattleNodeBuilder         (belligerent grouping, outcome parsing)
  ├── CharacterNodeBuilder      (lifecycle chain, title/rank progression)
  └── ...
```

Each builder:
1. Receives the raw infobox data, page metadata, and the `InfoboxDefinitionRegistry` definition for its template
2. Produces a `GraphNode` (with properties and temporal facets) and a `List<RelationshipEdge>`
3. Has full control over how fields are classified and what properties/edges are emitted
4. Can add type-specific properties (like `TradeRoute.Other objectsIds`) without polluting the generic path

`InfoboxGraphService` becomes a coordinator:
1. Iterates pages
2. Resolves the template type
3. Dispatches to the appropriate builder (or `DefaultNodeBuilder` if none registered)
4. Collects nodes + edges
5. Runs the existing post-processing (edge dedup, target type resolution, reverse labels, lineage computation)

### Registration

Builders register via a dictionary keyed by node type:

```csharp
Dictionary<string, INodeBuilder> _builders = new()
{
    [KgNodeTypes.TradeRoute] = new TradeRouteNodeBuilder(),
    [KgNodeTypes.Government] = new GovernmentNodeBuilder(),
    // ...
};

INodeBuilder _default = new DefaultNodeBuilder();
```

`DefaultNodeBuilder` contains the current generic loop logic — existing behaviour for all types that don't have a custom builder. No existing behaviour changes unless a custom builder is explicitly added.

### Migration path

1. Extract the current generic loop into `DefaultNodeBuilder` — pure move, no behaviour change
2. Extract `TradeRouteNodeBuilder` from the current `if (type == KgNodeTypes.TradeRoute)` patch
3. Add new builders incrementally as type-specific requirements arise
4. Each builder is independently testable

## Complexity

Medium. The generic loop is self-contained and well-understood. The refactor is mostly a structural move — the classification logic (`FieldSemantics`, `TemplateFields`, `InfoboxDefinitionRegistry`) stays unchanged. The builders consume the same inputs and produce the same outputs.

The risk is in edge cases where the generic loop's ordering matters (e.g. temporal facet assignment happening after all fields are processed). Each builder needs to replicate or call the shared temporal assembly. A `NodeBuilderBase` base class with `AssignFacetOrder`, `ComputeEnvelope`, etc. as protected methods would handle this.

## Not in scope

- Changing the ETL pipeline phases or their ordering
- Changing the `GraphNode` or `RelationshipEdge` models
- Changing `FieldSemantics` or `TemplateFields` — these remain the field-level metadata, consumed by builders
- Adding new node types or template mappings
