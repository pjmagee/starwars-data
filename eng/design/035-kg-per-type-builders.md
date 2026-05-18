# Design-035: Knowledge Graph — Per-Type Node Builders

> Renumbered from Design-007 on 2026-05-18 (resolved a duplicate-number collision).

**Status:** Implemented (2026-04-26); verified still in place 2026-05-18. Every `KgNodeTypes` constant has its own `INodeBuilder` under `NodeBuilders/Types/` (70 builder files as of 2026-05-18 — grown from the 45 listed below as new node types were added). `InfoboxGraphService` remains the coordinator with an explicit registry (`RegisterAllBuilders`, `_builders.GetValueOrDefault(...)`) — no monolithic generic loop, no reflection. Type-specific extraction logic (per-type-specific behaviour beyond the shared field loop) lands incrementally per builder.
**Date:** 2026-04-06 (proposal); 2026-04-26 (full decomposition landed)
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

## Implementation (2026-04-26)

Landed on branch `refactor/kg-per-type-node-builders`. Pure structural move — no behaviour change. The brittle monolithic generic loop in the original `InfoboxGraphService.BuildGraphAsync` is gone; per-page extraction is now dispatched through an explicit per-type registry.

### Layout

| File | Role |
| --- | --- |
| `NodeBuilders/INodeBuilder.cs` | Builder contract: `NodeType` + `Build(NodeBuilderContext)` |
| `NodeBuilders/NodeBuilderContext.cs` | Per-page input record (PageId, infobox data items, definition, lookups) |
| `NodeBuilders/NodeBuilderResult.cs` | `(GraphNode, IReadOnlyList<RelationshipEdge>)` output |
| `NodeBuilders/NodeBuilderBase.cs` | Default `Build()` (shared field loop) + protected helpers (temporal parsing, link resolution, primary-link extraction, facet ordering, label normalisation) + `OnRelationshipExtracted` and `OnFinalize` virtual hooks |
| `NodeBuilders/Types/<X>NodeBuilder.cs` | One file per `KgNodeTypes` constant — 44 type-specific builders + `UnknownNodeBuilder` (fallback) |
| `KnowledgeGraph/InfoboxGraphService.cs` | Coordinator: page iteration, builder dispatch, post-processing (edge filtering, dedup, lineage closures, indexes, bidir view, label registry). 0 monolithic logic remaining |

### Registered builders (45 total)

```text
People         Character, Person, Family, Species
Geography      CelestialBody, Location, City, Structure, System, Sector, Region, Nebula
Politics       Government, Organization, Military
Events         Battle, War, Campaign, Mission, Duel, Election, Event, Treaty, Era, Year
Vehicles       Starship, StarshipClass, SpaceStation, Vehicle, AirVehicle, GroundVehicle, TradeRoute
Things         Weapon, Lightsaber, Device, Artifact, Droid
Qualifier      TitleOrPosition, ForcePower, LightsaberForm
Media          Book, Movie, Comic, Game
Fallback       Unknown
```

### Dispatch

`InfoboxGraphService` constructor calls `RegisterAllBuilders()` which hand-lists every builder — explicit, no reflection. Per-page dispatch is one line:

```csharp
var builder = _builders.GetValueOrDefault(context.Type, _unknownBuilder);
var result = builder.Build(context);
```

Pages whose template type isn't a recognised `KgNodeTypes` constant fall back to `UnknownNodeBuilder`, which behaves identically to the generic field loop in the base.

### Type-specific behaviour today

Only `TradeRouteNodeBuilder` overrides `OnRelationshipExtracted` to emit ordered `{label}Ids` waypoint sequences. Every other type currently inherits the base `Build()` unchanged. Specific behaviour for the design's identified types (Battle belligerent grouping, Government institutional lifecycle, System orbital hierarchy, CelestialBody destruction events, Character lifecycle chain) lands incrementally — the per-type files give each one a stable home for that work without disturbing siblings.

### How to add type-specific behaviour to a builder

1. Pick the `<Type>NodeBuilder.cs` file.
2. Override one of the protected virtual hooks:
   - `OnRelationshipExtracted` — runs after primary-link extraction for each relationship field. Use this to emit additional type-specific properties (the TradeRoute waypoint pattern).
   - `OnFinalize` — runs after the node + edges are fully assembled. Use this to mutate either before they're handed to the coordinator.
   - Override `Build` outright if the generic field loop is wrong for this type. Rare.
3. No registration change needed — the constructor already wires every type.

### How to add a new node type

1. Add the constant to `KgNodeTypes`.
2. Create `NodeBuilders/Types/<X>NodeBuilder.cs` (5 lines: namespace + class + `NodeType` override).
3. Add a single `RegisterBuilder(new XNodeBuilder())` line to `RegisterAllBuilders()` in `InfoboxGraphService`.
4. No DI changes needed — builders are stateless and constructed inline.

### Verification

- Solution builds clean: `dotnet build src/StarWarsData.slnx` — 0 warnings, 0 errors.
- Unit tests pass: 67/67 (`dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"`).
- Integration tests pass: 62/62 (`dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Integration"`).
