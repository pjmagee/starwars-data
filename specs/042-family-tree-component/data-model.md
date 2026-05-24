# Phase 1 Data Model — Family Tree Component

**Branch**: `feature/042-family-tree-component` | **Date**: 2026-05-24 | **Plan**: [plan.md](./plan.md)

This file maps the wire shape (`{ rootId, rootName, people[], kinship[], limitations }`) onto C# records on the server, and documents the validation / projection rules that turn `kg.edges` rows into that shape. Pair with [contracts/family-tree-endpoint.md](./contracts/family-tree-endpoint.md) (HTTP wire) and [contracts/render-family-tree-tool.md](./contracts/render-family-tree-tool.md) (AI tool wire).

---

## C# records

All records live under `src/StarWarsData.Models/AI/Ask.cs` (alongside the other Ask descriptors). The descriptor and the endpoint response share the people / kinship / limitations sub-records — the descriptor wraps them in the Ask envelope (`Title`, `MobileSummary`, `References`), the endpoint returns them raw.

### `FamilyTreeDescriptor`

```csharp
public sealed record FamilyTreeDescriptor(
    string Title,                           // human-readable, displayed above the chart
    int RootEntityId,                       // PageId of the focal Character
    string RootEntityName,                  // display name
    int MaxDepth,                           // clamped [1..5], default 3
    string? Continuity,                     // "Canon" | "Legends" | null = all
    string MobileSummary,                   // REQUIRED markdown for <md viewports
    List<FamilyTreePerson> People,          // chart data — see below
    List<FamilyTreeKinshipEntry>? Kinship,  // optional — for the `kinship` plugin (has_relative entries)
    FamilyTreeLimitations Limitations,      // metadata block — see below
    List<Reference>? References             // standard reference list
);
```

**Validation**:

- `Title` non-empty.
- `RootEntityId > 0`.
- `MaxDepth` clamped `[1..5]` at construction (use a setter / factory; do not trust the AI agent's claim).
- `MobileSummary` non-empty — Mobile fallback is mandatory.
- `People` MUST contain an entry where `Id == RootEntityId.ToString()`.
- For every entry in `People`, `rels.spouses[]`, `rels.parents[]`, `rels.children[]` MUST reference IDs that are also in `People` (bidirectional-link invariant) OR be a synthetic stub (suffix `-stub`).

### `FamilyTreePerson`

```csharp
public sealed record FamilyTreePerson(
    string Id,                                       // PageId as string (family-chart requires string IDs)
    FamilyTreePersonData Data,                       // card-visible fields
    FamilyTreeRels Rels                              // relationship arrays
);

public sealed record FamilyTreePersonData(
    string Gender,                                   // "M" | "F" — family-chart constraint, see research.md R-5
    [property: JsonPropertyName("first name")]
    string FirstName,
    [property: JsonPropertyName("last name")]
    string LastName,
    string? WikiUrl,
    string? ImageUrl,
    int PageId                                       // for the click-handler navigation
);

public sealed record FamilyTreeRels(
    List<string>? Parents,
    List<string>? Spouses,                           // partner_of ∪ spouse_of ∪ married_to (premium spouse-link-text plugin re-labels the rendered edge)
    List<string>? Children
);
```

**Notes**:

- `JsonPropertyName` is required on `first name` / `last name` because family-chart's JS code reads those exact keys (with the space).
- `Gender` is `"M" | "F"` ONLY at this surface. The `"M"` default for missing/ambiguous data is a library constraint; the projection records the PageId in `limitations.missingGenders` so the limitations chip can surface the gap.
- `Rels` properties are nullable lists (omit empty arrays from the wire — family-chart accepts missing keys as empty).

### `FamilyTreeKinshipEntry` (premium kinship plugin)

```csharp
public sealed record FamilyTreeKinshipEntry(
    string PersonId,                                 // family-chart person ID
    string RelativeId,                               // family-chart person ID (must also be in People[])
    string Relationship                              // free-text label from kg.edges, e.g. "Cousin", "Niece"
);
```

**Purpose**: `has_relative` edges (cousins, in-laws, etc.) don't fit family-chart's `{parents, spouses, children}` model. The premium `kinship` plugin renders them as a secondary view ("show Anakin's extended kin"). One entry per directed edge.

### `FamilyTreeLimitations`

```csharp
public sealed record FamilyTreeLimitations(
    List<int> MissingGenders,                        // PageIds defaulted to "M"
    List<string> AdoptiveRelationsExcluded,          // "{childPageId}→{parentPageId} via {family node name}"
    bool TruncatedAtDepth,                           // BFS hit maxNodes before reaching maxDepth
    bool CycleFallback                               // renderer crashed/looped; fallback to render_graph Tree mode
);
```

**Renders as**: a single MudAlert advisory chip in `FamilyTreeView.razor`, e.g. *"Missing gender on 3 characters · 1 adoptive relation excluded · Tree truncated"*.

---

## Mongo projection rules

`BuildFamilyTreeAsync(int rootId, int maxDepth, string? continuity, string? realm, int maxNodes = 200, CancellationToken ct)` lives in `KnowledgeGraphQueryService.cs`, sibling of `QueryGraphAsync`.

### Step 1 — BFS the family neighbourhood

Run two passes per visited node (until `maxDepth` levels deep or `maxNodes` reached):

- **Outbound**: `kg.edges` where `from == nodeId` and `label IN [parent_of, child_of, sibling_of, partner_of, spouse_of, married_to, family, has_relative]`.
- **Inbound**: `kg.edges` where `to == nodeId` and same `label IN` clause.

Filters applied identically:

```text
continuity ∈ { Canon, Legends, null }  → match on edge.continuity (null = both)
realm      ∈ { StarWars, RealWorld, null } → match on edge.realm
```

Skip edges with `meta.contentHash == null` (stub-only nodes per [feedback_kg_nodes_stubs.md](../../C:/Users/patri/.claude/projects/d--Projects-pjmagee-starwars-data/memory/feedback_kg_nodes_stubs.md) — soft-handle; do not crash).

### Step 2 — Project each visited node to `FamilyTreePerson`

For each `nodeId` in the visited set:

| Output field | Source |
| --- | --- |
| `Id` | `nodeId.ToString()` |
| `Data.PageId` | `kg.nodes._id` |
| `Data.FirstName` / `Data.LastName` | split `kg.nodes.name` on the last whitespace; if no whitespace, use full name as `FirstName` and empty `LastName` (collision-prone, accept the imperfection — same approach as the prior MIT plan) |
| `Data.WikiUrl` | `kg.nodes.wikiUrl` |
| `Data.ImageUrl` | `kg.nodes.imageUrl` (may be null) |
| `Data.Gender` | `raw.pages.infobox.Data` where `Label == "Gender"`; map `"Male"`→`"M"`, `"Female"`→`"F"`, else `"M"` + append `nodeId` to `Limitations.MissingGenders` (research.md R-5) |
| `Rels.Parents` | nodeIds `B` where `(B, parent_of, nodeId)` OR `(nodeId, child_of, B)` exists. Dedup. |
| `Rels.Children` | nodeIds `B` where `(nodeId, parent_of, B)` OR `(B, child_of, nodeId)` exists. Dedup. |
| `Rels.Spouses` | `partner_of` ∪ `spouse_of` ∪ `married_to` (both directions, dedup). Premium's `spouse-link-text` plugin renders the *label* per edge so we keep the underlying edge label on a side channel for the plugin. |

### Step 3 — Kinship entries (premium `kinship` plugin)

For every `has_relative` edge `(A, has_relative, B)` where both A and B are in the visited set:

- Emit `FamilyTreeKinshipEntry(PersonId=A, RelativeId=B, Relationship=edge.meta.qualifier ?? "Relative")`.
- Skip if A == B (self-edge — bad data).

`has_relative` does NOT translate into `Rels.Parents`/`Children`/`Spouses` — it lives in the kinship block only.

### Step 4 — `family` membership edges

`family` edges (e.g. `Leia` ↔ `Skywalker family`) are NOT translated into `Rels.parents` (they would create false parent-child links). Two cases:

- If the `family` node represents an *adoptive household* relationship (rare; e.g. `Leia` ↔ `Organa family` while parent_of points to Anakin/Padmé), record `"{childPageId}→{parentPageId via family node}"` in `Limitations.AdoptiveRelationsExcluded`.
- Otherwise drop silently.

### Step 5 — Enforce bidirectional linking

After step 2, scan every `(person, rels.spouses[])` pair: if `personA.spouses` includes `personB.Id`, then `personB.spouses` MUST include `personA.Id`. Same for `parents`/`children`. Repair any one-sided reference.

For references to PageIds NOT present in `People[]` (truncation or genuinely-absent kg.nodes):

- Emit a synthetic stub `FamilyTreePerson` with `Id = $"{missingPageId}-stub"`, `Data.PageId = missingPageId`, `Data.Gender = "M"`, `Data.FirstName = "Unknown"`, `Data.LastName = "Unknown"`, `Rels` empty.
- Append `missingPageId` to `Limitations.MissingGenders` (the stub is genuinely missing data).
- The stub's PageId carries through so the card click navigates to `/knowledge-graph/nodes/{pageId}` (research.md R-7).

### Step 6 — Truncation

If the BFS visited-set hit `maxNodes` before exhausting `maxDepth`, set `Limitations.TruncatedAtDepth = true`. Do NOT throw; the chart should render as much as it has.

---

## Validation rules (enforced in unit tests)

- **Root present**: `People` MUST contain an entry where `Id == RootEntityId.ToString()`.
- **No orphan references**: every ID referenced in any `Rels.*` array MUST exist in `People[]` (real or stub).
- **Bidirectional spouses**: if `personA.Rels.Spouses` includes `B`, then `personB.Rels.Spouses` includes `A`.
- **Parent ↔ child symmetry**: if `personA.Rels.Parents` includes `B`, then `personB.Rels.Children` includes `A`.
- **No self-edges**: `personA.Rels.Spouses` MUST NOT include `personA.Id`.
- **Kinship targets are people**: every `Kinship[].PersonId` and `Kinship[].RelativeId` MUST exist in `People[]`.

All six rules are unit-tested against the `ApiFixture` seed (Skywalker / Solo / Naberrie / Lars) per [plan.md § Testing](./plan.md#technical-context).

---

## Edge cases captured in unit tests

| Case | Fixture data | Expected output |
| --- | --- | --- |
| Anakin (root) | 1 spouse (Padmé), 2 children (Luke, Leia), 1 parent (Shmi), no step-parents | `People[Anakin].Rels.Spouses=[Padmé.Id]`, `.Parents=[Shmi.Id]`, `.Children=[Luke.Id, Leia.Id]` |
| Padmé (only as Anakin's spouse) | spouse of Anakin, mother of Luke + Leia, no parents in seed | Present in `People[]` with bidirectional spouse link to Anakin and `Children=[Luke.Id, Leia.Id]` |
| Luke ↔ Leia siblings | shared parents Anakin + Padmé, no explicit `sibling_of` edge | Both have `Parents=[Anakin.Id, Padmé.Id]`; family-chart infers sibling visual from shared parents |
| Han + Leia + Ben Solo | Han spouses Leia; Ben child_of both | Subtree linked to Anakin's tree via Leia's spouse edge |
| Bidirectional repair | seed has only `(Anakin, partner_of, Padmé)`, no reverse | Output has BOTH `Anakin.Spouses=[Padmé.Id]` AND `Padmé.Spouses=[Anakin.Id]` |
| Truncation | seed truncated at maxNodes=3 | `Limitations.TruncatedAtDepth=true`; missing references emit `-stub` entries |
| Missing gender | Character with no `Gender` infobox field | `Data.Gender="M"`, PageId in `Limitations.MissingGenders` |
| Adoptive (Leia → Bail Organa) | `Leia` ↔ `Organa family` membership, no `parent_of` | `Bail` NOT in `Leia.Rels.Parents`; entry in `Limitations.AdoptiveRelationsExcluded` |

---

## Indexes

No new MongoDB indexes are required. The BFS uses the existing `kg.edges` indexes on `(from, label)` and `(to, label)` (per [Design-007 / Design-035](../035-kg-per-type-builders/spec.md)). The `raw.pages.infobox.Data.Label` read for `Gender` is one extra `findOne` per visited node — at maxNodes=200 that's 200 point queries on a `_id`-indexed collection, well within the 200 ms p95 budget.
