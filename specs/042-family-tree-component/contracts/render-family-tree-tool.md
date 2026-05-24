# Contract — `render_family_tree` AI tool

**Branch**: `feature/042-family-tree-component` | **Owner**: `ChartToolKit.cs` | **Tool-name constant**: `ToolNames.RenderFamilyTree`

The Ask agent's `render_family_tree` tool. Wraps the `/api/RelationshipGraph/family-tree/{pageId}` endpoint and returns a `FamilyTreeDescriptor` for the Frontend to render. Sibling of `render_graph` and `render_path` in the existing toolkit.

---

## Tool description (LLM-facing)

Verbatim text the agent sees. Follows the opinionated **REQUIRED PRECONDITION / HARD ANTI-PATTERN** style used by `render_graph` and `render_path`:

```text
Render a marriage-aware, generation-aligned family tree for a single
focal Character. USE THIS for kinship questions: "family tree", "lineage",
"ancestry", "genealogy", "who is X's parent/child/spouse", "trace X's
heritage". Spouses cluster as paired units above their kids; generations
align by genealogical depth, not BFS depth from the focal node.

REQUIRED PRECONDITION:
  1. Call search_entities(query) FIRST to resolve the user's name to a
     PageId.
  2. Verify the resolved entity's `type == "Character"`. If it is anything
     else (Family aggregate, Organization, Government, …), STOP and emit
     a markdown summary instead of calling this tool.

HARD ANTI-PATTERNS — do not call this tool for:
  - A Family aggregate node (e.g. "Skywalker family"). Use search_entities
    to disambiguate and pick the specific Character root the user named.
  - Political hierarchies, military command chains, organization rosters,
    or any non-kinship relationship. Use render_graph (Tree mode).
  - Multiple roots in one tree. The tool renders ONE focal Character and
    their genealogy. For a comparison across families, render two trees.

The tool takes NO `labels` / `enabledLabels` parameters. Family edge
labels are fixed server-side (parent_of, child_of, sibling_of,
partner_of, spouse_of, married_to, family, has_relative). Any
client-supplied label list is ignored.
```

---

## Input schema

```json
{
  "type": "object",
  "required": ["rootEntityId", "rootEntityName", "title", "mobileSummary"],
  "properties": {
    "rootEntityId": {
      "type": "integer",
      "minimum": 1,
      "description": "PageId of the focal Character (resolved via search_entities)."
    },
    "rootEntityName": {
      "type": "string",
      "minLength": 1,
      "description": "Display name of the focal Character. Used as the chart caption."
    },
    "title": {
      "type": "string",
      "minLength": 1,
      "description": "Caption above the chart, e.g. \"Skywalker family tree centered on Anakin\"."
    },
    "mobileSummary": {
      "type": "string",
      "minLength": 1,
      "description": "REQUIRED markdown bullet list shown on <md viewports. The chart does not render on mobile."
    },
    "maxDepth": {
      "type": "integer",
      "minimum": 1,
      "maximum": 5,
      "default": 3,
      "description": "Generations to expand in each direction (ancestors + descendants)."
    },
    "continuity": {
      "type": "string",
      "enum": ["Canon", "Legends"],
      "description": "Continuity filter. Omit for both."
    }
  },
  "additionalProperties": false
}
```

**Hard rejection** (returns a markdown fallback, not the descriptor):

- `rootEntityId` resolves to a non-Character entity (the tool internally re-checks via `kg.nodes.type` before calling the endpoint).
- Endpoint returns `400`/`404`.

**Soft handling**: `maxDepth` outside `[1..5]` is clamped silently (matches the endpoint's behaviour).

---

## Output schema

```json
{
  "type": "object",
  "required": ["descriptor"],
  "properties": {
    "descriptor": {
      "$ref": "#/definitions/FamilyTreeDescriptor"
    }
  }
}
```

**Metadata-only pattern** (matches every other `render_*` tool in `ComponentToolkit` — `render_graph`, `render_path`, `render_table`, `render_infobox`, `render_timeline`, etc.). The tool returns a descriptor populated **only** with the metadata fields the LLM supplied (`title`, `rootEntityId`, `rootEntityName`, `maxDepth` clamped to `[1..5]`, `continuity`, `mobileSummary`, `references`). `descriptor.people[]` / `descriptor.kinship[]` / `descriptor.limitations` are returned at their defaults (empty list / null / default record) — the **Frontend** (`FamilyTreeView.razor`, Phase 4) fetches the projection from `GET /api/RelationshipGraph/family-tree/{rootEntityId}` at render time and populates those fields client-side.

This keeps the toolkit dependency-free (`ComponentToolkit` has a zero-arg constructor and no DI on `KnowledgeGraphQueryService` / `HttpClient`) and aligns with the existing pattern. Citation resolution still works — `descriptor.references[]` is LLM-supplied (sourced from `search_entities` results) and the existing `CitationResolver` walks it downstream of the tool call.

---

## Tool registration

- Lives in `src/StarWarsData.Services/AI/Toolkits/ChartToolKit.cs` (cross-agent toolkit per [CLAUDE.md § Conventions](../../../CLAUDE.md)). It is NOT in the SP-4 sidebar's toolkit (research.md R-9, Design-022 § Toolkit).
- Tool name constant: `public const string RenderFamilyTree = "render_family_tree";` in `ToolNames.cs`.
- Registered in `AskAIAgent.cs` alongside `render_graph` / `render_path` / etc.

---

## Agent prompt routing (AskAIAgent system prompt addition)

A new routing block above the existing GRAPH VISUALIZATION WORKFLOW:

```text
FAMILY-TREE ROUTING:
  Kinship phrasing — "family tree", "lineage", "ancestry", "genealogy",
  "parent/child/spouse/sibling", "trace heritage" — routes to
  render_family_tree. Required preconditions: (1) search_entities to
  resolve the user's name; (2) verify the resolved type is Character.
  If the resolved type is Family/Organization/Government, fall through
  to a markdown summary; do NOT call render_graph for kinship questions.

Everything else that today routes to render_graph stays there. The
narrow-labels guidance for non-family graphs is independent and remains.
```

---

## Performance + cost

- The tool itself runs one `search_entities` + one `kg.nodes.findOne` (type check) + one HTTP call to the endpoint. No extra LLM round-trip beyond the agent's tool-call.
- `UseFunctionInvocation.MaximumIterationsPerRequest = 8` budget: one render_family_tree call = 1 iteration. Same as other `render_*` tools.
- The bigger compute cost is the BFS endpoint (≤ 200 ms p95) — already covered by the endpoint contract.

---

## Test coverage

- **Unit** (in `FamilyTreeProjectionTests.cs`): the descriptor's invariants — `MaxDepth` clamping, `MobileSummary` non-empty enforcement, `People[]` contains root, bidirectional spouse / parent ↔ child symmetry. These are projection-level tests; the tool itself just wraps the descriptor.
- **Integration** (in `FamilyTreeEndpointTests.cs`): end-to-end against Testcontainers Mongo — agent calls tool → tool calls endpoint → response matches contract.
- **Agent** (in `FamilyTreeAgentRoutingTests.cs`): live agent picks `render_family_tree` (not `render_graph`) for prompts like "Show me the Skywalker family tree centered on Anakin Skywalker". One representative test; not every prompt phrasing.
