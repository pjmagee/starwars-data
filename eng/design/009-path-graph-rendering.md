# Design 009: Path Graph Rendering

**Status:** Implemented  
**Date:** 2026-04-09

## Problem

When a user asks "Trace the shortest connection from Darth Sidious to Luke Skywalker", the AI agent:

1. Resolves both entities via `search_entities` → gets two `PageId` values
2. Calls `find_connections(entityId1, entityId2)` → gets the shortest path as `ConnectionsDto`
3. Needs to **visualize** the path — but the only graph tool is `render_graph`

**The mismatch:** `render_graph` takes a single `rootEntityId` + `maxDepth` and does a **full BFS neighborhood expansion**. For the Sidious→Luke path (Sidious → master_of → Vader → parent_of → Luke), calling `render_graph(rootEntityId=Sidious, maxDepth=2, labels=["master_of","parent_of"])` would pull in Dooku, Maul, other apprentices, Leia, Han — every node reachable in 2 hops across those labels. The 3-node path drowns in a 30+ node neighborhood.

The agent's only alternative today is to describe the path in markdown text, losing the interactive D3 visualization entirely.

## Goal

Enable the AI agent to render a **focused, scoped graph** showing only the specific nodes and edges from a path query (or any explicit node set), using the existing D3 graph infrastructure.

## Design

### Approach: Extend `GraphDescriptor` with a `path` mode

Add a new optional field to `GraphDescriptor` that carries pre-resolved path data. When present, the frontend skips the BFS API call and renders the provided nodes/edges directly.

This approach was chosen over alternatives (see Alternatives Considered below) because it:
- Reuses the existing `GraphViewer` D3 component unchanged
- Reuses the existing `AskGraphView` component with minimal changes
- Avoids a new API endpoint (the data is already available from `find_connections`)
- Follows the existing pattern where some descriptors carry inline data (e.g., `ChartDescriptor`, `DataTableDescriptor`)

### Data flow

```
User: "Trace connection from Sidious to Luke"
  ↓
Agent calls search_entities("Darth Sidious") → PageId=123
Agent calls search_entities("Luke Skywalker") → PageId=456
  ↓
Agent calls find_connections(entityId1=123, entityId2=456, maxHops=3)
  → ConnectionsDto { Connected: true, PathLength: 3, Path: [...] }
  ↓
Agent calls render_path(
    title: "Connection: Darth Sidious → Luke Skywalker",
    fromEntityId: 123, fromEntityName: "Darth Sidious",
    toEntityId: 456, toEntityName: "Luke Skywalker",
    pathSteps: [
      { fromId: 123, toId: 789, label: "master_of", evidence: "..." },
      { fromId: 789, toId: 456, label: "parent_of", evidence: "..." }
    ]
)
  → GraphDescriptor with inline PathData
  ↓
Frontend AskGraphView receives descriptor
  → Detects PathData is present
  → Builds RelationshipGraphResult directly from PathData (no API call)
  → Renders via existing GraphViewer/D3
```

### Changes implemented

#### 1. `GraphLayoutMode` enum (Ask.cs)

Replaced the string-based `LayoutMode` with a proper C# enum:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphLayoutMode { Force, Tree, Path }
```

Each value has a `[Description]` explaining its purpose. `GraphDescriptor.LayoutMode` is now `GraphLayoutMode` with `[JsonConverter(typeof(JsonStringEnumConverter))]` for JSON round-tripping. Old stored sessions with lowercase `"force"` deserialize correctly (case-insensitive read). `GraphViewer.razor` converts to lowercase via `.ToString().ToLowerInvariant()` before passing to D3 JS.

#### 2. `ConnectionStepDto` extended with IDs and types (ToolkitDtos.cs)

```csharp
public sealed record ConnectionStepDto(
    int FromId, string From, string FromType,
    int ToId, string To, string ToType,
    string Label, string Evidence
);
```

`FindConnectionsAsync` and `ExpandFrontierAsync` in `KnowledgeGraphQueryService` now carry `fromType`/`toType` through the BFS traversal, sourced from `RelationshipEdge.FromType`/`ToType`.

#### 3. `PathData` / `PathStep` models (Ask.cs)

`PathStep` includes `FromType`/`ToType` so D3 can color-code nodes by type without an extra API call. The `PathStepInput` record mirrors this for the `render_path` tool input.

#### 4. `render_path` tool (ComponentToolkit)

New tool registered as `"render_path"` in `AsAIFunctions()`. Constructs a `GraphDescriptor` with `LayoutMode = GraphLayoutMode.Path` and inline `PathData`. The `render_graph` tool now parses the string parameter to `GraphLayoutMode` and rejects `Path` mode (clamped to `Force`).

#### 5. Path layout mode in D3 (d3-graph.js)

New `layoutPathPositions()` function arranges nodes left-to-right by BFS depth with 220px spacing. The path layout:

- Reuses `computeBfsDepths()` from tree layout
- Resolves link source/target IDs to objects manually (no `forceLink`)
- Uses straight-line edges (same `<line>` elements as force layout, positioned via `tick()`)
- Disables drag (fixed positions, like tree)
- Auto-fits to screen after initial render

#### 6. `AskGraphView.razor` — PathData branch

When `Descriptor.PathData` is present, `BuildGraphFromPath()` constructs a `RelationshipGraphResult` inline — no API call. Path mode hides depth/label controls (not passed to `GraphViewer`).

#### 7. `GraphViewer.razor` — Enum parameter

`LayoutMode` parameter changed from `string` to `GraphLayoutMode`. Default is `GraphLayoutMode.Force`. The JS interop call converts via `.ToString().ToLowerInvariant()`.

#### 8. `Ask.razor` — Tool dispatch

`"render_path"` added to the tool name check and falls through to the same `"graph"` visualization type as `"render_graph"` (both produce `GraphDescriptor`).

#### 9. Agent system prompt (AgentPrompt.cs)

Updated guidance:

- `render_path` documented in the FRONTEND-FETCHED tools section
- "How is X related to Y?" now routes to `find_connections → render_path` instead of `render_markdown`
- `render_graph` explicitly notes "Do NOT use for shortest-path results — use render_path instead"
- `render_path` usage documented in the KEY RULES section

## Alternatives considered

### A. New API endpoint `GET /api/RelationshipGraph/path/{fromId}/{toId}`

**Rejected:** Duplicates work — `find_connections` already did the BFS. No reason to re-execute it server-side.

### B. Add `nodeIds` filter to existing `QueryGraphAsync`

**Rejected:** BFS with post-filtering is wasteful. The caller already knows exactly what to render.

### C. Separate `PathViewer` Blazor component + dedicated D3 renderer

**Rejected:** The existing `GraphViewer` + D3 renderer handle small node sets well with the new `path` layout mode. No code duplication needed.

### D. Render path as markdown/table only (no graph)

**Rejected:** Loses the interactive visual graph experience.

## Follow-ups

- **Batch node image endpoint** — Path nodes currently render without wiki thumbnails (type + name only). A batch `/api/RelationshipGraph/nodes?ids=...` endpoint could enrich with `imageUrl`.
- **Edge evidence on hover** — Path steps carry `evidence` text that could be shown in a D3 tooltip.
- **Multi-path rendering** — k-shortest-paths between two entities. Out of scope.

## Testing

- **Unit:** Verify `render_path` produces correct `GraphDescriptor` with `PathData`
- **Integration:** `find_connections` returns IDs/types in `ConnectionStepDto`; `AskGraphView` builds correct `RelationshipGraphResult` from `PathData`
- **Manual/E2E:** Ask "How is Darth Sidious connected to Luke Skywalker?" → agent uses `find_connections` then `render_path` → focused 3-node path graph renders left-to-right
