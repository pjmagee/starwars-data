# Contract — `GET /api/RelationshipGraph/family-tree/{pageId}`

**Branch**: `feature/042-family-tree-component` | **Owner**: `RelationshipGraphController.cs` | **Service**: `KnowledgeGraphQueryService.BuildFamilyTreeAsync`

The HTTP contract for the family-tree endpoint. Consumed by:

- The Frontend `FamilyTreeView.razor` component (browser → ApiService internal call).
- The Ask agent's `render_family_tree` tool, which calls this endpoint to get the data it returns inside the `FamilyTreeDescriptor` envelope.

---

## Request

```text
GET /api/RelationshipGraph/family-tree/{pageId}
    ?maxDepth=3
    &continuity=Canon
    &realm=StarWars
```

### Path parameters

| Name | Type | Required | Constraints |
| --- | --- | --- | --- |
| `pageId` | `int` | yes | PageId of the focal **Character** entity. The controller MUST reject requests where the resolved entity's `kg.nodes.type != "Character"` with `400 Bad Request` and a JSON body `{ "error": "RootMustBeCharacter", "actualType": "<type>" }`. |

### Query parameters

| Name | Type | Required | Default | Constraints |
| --- | --- | --- | --- | --- |
| `maxDepth` | `int` | no | `3` | Clamped to `[1..5]` server-side. Values outside this range are silently clamped, NOT rejected. |
| `continuity` | `string` | no | `null` (= all) | `"Canon"` or `"Legends"`. Any other value → `400 Bad Request`. |
| `realm` | `string` | no | `null` (= all) | `"StarWars"` or `"RealWorld"`. Any other value → `400 Bad Request`. |

### Headers

- `X-User-Id` — required (set by the Frontend's `DelegatingHandler` per [ADR-001](../../../eng/adr/001-internal-api-auth.md)). Missing → `401 Unauthorized`.

---

## Response

### `200 OK`

```json
{
  "rootId": "452390",
  "rootName": "Anakin Skywalker",
  "people": [
    {
      "id": "452390",
      "data": {
        "gender": "M",
        "first name": "Anakin",
        "last name": "Skywalker",
        "wikiUrl": "https://starwars.fandom.com/wiki/Anakin_Skywalker",
        "imageUrl": "https://static.wikia.nocookie.net/.../Anakin_RotS.png",
        "pageId": 452390
      },
      "rels": {
        "parents": ["452391"],
        "spouses": ["452392"],
        "children": ["452393", "452394"]
      }
    },
    {
      "id": "452391",
      "data": {
        "gender": "F",
        "first name": "Shmi",
        "last name": "Skywalker Lars",
        "pageId": 452391
      },
      "rels": {
        "spouses": ["452395"],
        "children": ["452390"]
      }
    }
  ],
  "kinship": [
    { "personId": "452390", "relativeId": "452399", "relationship": "Uncle" }
  ],
  "limitations": {
    "missingGenders": [],
    "adoptiveRelationsExcluded": ["452394→Bail Prestor Organa via Organa family"],
    "truncatedAtDepth": false,
    "cycleFallback": false
  }
}
```

### Fields

| Field | Type | Notes |
| --- | --- | --- |
| `rootId` | `string` | Echo of `{pageId}.ToString()`. |
| `rootName` | `string` | `kg.nodes.name` of the root. |
| `people` | `FamilyTreePerson[]` | See [data-model.md § `FamilyTreePerson`](../data-model.md#familytreeperson). Always contains a record where `id == rootId`. |
| `kinship` | `FamilyTreeKinshipEntry[] \| null` | Omitted (null on the wire) if empty. `personId` and `relativeId` are always present in `people[]`. |
| `limitations` | `FamilyTreeLimitations` | Always present, even when empty (`missingGenders: []`, `adoptiveRelationsExcluded: []`, `truncatedAtDepth: false`, `cycleFallback: false`). |

### Error responses

| Status | When | Body |
| --- | --- | --- |
| `400` | `pageId` resolves to a non-Character entity | `{ "error": "RootMustBeCharacter", "actualType": "Family" }` |
| `400` | `continuity` or `realm` is not in the allowed set | `{ "error": "InvalidQueryParameter", "name": "continuity", "value": "Wookies" }` |
| `401` | `X-User-Id` header missing | empty |
| `404` | `pageId` does not exist in `kg.nodes` | `{ "error": "RootNotFound", "pageId": 999999 }` |
| `500` | Mongo or projection failure | `{ "error": "ProjectionFailed", "message": "<safe error>" }` (no stack traces) |

---

## Invariants (verified in integration tests)

1. Response is JSON; `Content-Type: application/json; charset=utf-8`.
2. Every `id` in `people[]` is a unique non-empty string.
3. Real entries have `id == data.pageId.ToString()`. Synthetic stubs have `id == "{data.pageId}-stub"`.
4. For every `rels.parents/children/spouses` entry on person A, the referenced person B is also in `people[]`, and the bidirectional link holds:
   - `B in A.rels.spouses` ↔ `A in B.rels.spouses`
   - `B in A.rels.parents` ↔ `A in B.rels.children`
5. `kinship[*].personId` and `kinship[*].relativeId` are both present in `people[]`.
6. The root entity (id == rootId) MUST be present.
7. Continuity + realm passthrough: if the request specifies `continuity=Canon`, no edge in the resulting tree has a non-Canon `continuity` field. (Verified against the Testcontainers seed.)

---

## Performance contract

- **Latency target**: p95 ≤ 200 ms for `maxDepth ≤ 5`, `maxNodes ≤ 200`, warm cache. Mirrors `QueryGraphAsync`. Measured via Aspire OTel histogram.
- **Concurrency**: read-only; no shared mutable state; safe for the existing per-endpoint rate-limit middleware.
- **Caching**: no response cache for v1. The agent calls this once per turn; the Frontend re-fetches on continuity/realm change. Revisit if Aspire OTel shows repeat calls at scale.

---

## Versioning

- v1 returns the shape above.
- A future v2 (asymmetric ancestor/descendant depth — see research.md R-8) will add `?ancestorDepth=&descendantDepth=` query params and deprecate `maxDepth`. Backwards compatibility: if only `maxDepth` is provided, behaviour is unchanged.
- Breaking changes to `people[].data.*` would require a versioned path (`/v2/family-tree/{pageId}`) — v1 is now the lockstep contract with the vendored `family-chart-premium` JS.
