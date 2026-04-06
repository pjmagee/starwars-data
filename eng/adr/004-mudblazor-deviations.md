# ADR-004: Deviations from Standard MudBlazor Components

**Status:** Accepted
**Date:** 2026-04-05
**Decision maker:** Patrick Magee

## Context

The Frontend and Admin projects use [MudBlazor](https://mudblazor.com/) (v9.2.0) as the primary UI component library. MudBlazor provides a comprehensive Material Design component set that should be the default choice for all UI work in this repository.

However, there are isolated places where we deliberately step outside the library and implement custom HTML/CSS or raw `<button>`/`<div>` elements instead of using the corresponding MudBlazor component. This ADR exists to:

1. Document the current deviations, the component they replace, and the specific reason
2. Establish the rule that **any future deviation must be recorded here** with its justification
3. Make it easy to revisit deviations when MudBlazor adds features that would close the gap

## Principle

**MudBlazor is the default.** If a MudBlazor component exists for the use case, use it — even if it requires some inline `Style=` or `Class=` tweaks. Only reach for raw HTML/CSS when the gap between what MudBlazor offers and what the design requires cannot be closed with the component's public API (parameters, `Class`, `Style`, theming).

When a deviation is introduced:

- Add an entry to the **Catalogue** section of this ADR with the file, the component being replaced, and the concrete reason
- Include a **Revisit when** line describing what would allow the deviation to be removed (e.g., "MudChip adds a `Dense` or sub-`Small` size")
- Prefer wrapping the custom element in a single CSS class scoped to the page/component so the deviation is localised

## Catalogue

### Era chip bar — `GalaxyMapUnified.razor`

- **Location:** [src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor) — `.era-chip` CSS class and raw `<button>` elements inside `.timeline-eras`
- **MudBlazor component replaced:** `MudChip<T>` (optionally inside `MudChipSet<T>`)
- **Reason:** The timeline bar at the bottom of the galaxy map is a compact, horizontally-scrollable density visualisation. The era chip row sits above the density ticks with a total target height of ~22px and `gap: 4px` between chips. `MudChip` exposes only `Size.Small / Medium / Large`; `Size.Small` renders at ~26–28px with larger horizontal padding and applies default margins to adjacent chips, which breaks the compact layout. Reclaiming the sizing would require per-chip `Style="height:22px;font-size:12px;padding:0 10px;"` plus a global override to zero out `.mud-chip` margins — which is more custom CSS than the raw-button approach, scattered across every render instead of localised in one `.era-chip` class.
- **What we keep consistent with MudBlazor:** the `.era-chip.active` state uses `var(--mud-palette-primary)` so it picks up the theme colour automatically.
- **Revisit when:** MudBlazor introduces a denser chip size (`Dense` parameter, `Size.ExtraSmall`, or equivalent) that renders at ≤22px with tight padding and removes default margins in horizontal groups.

### Density tick bar — `GalaxyMapUnified.razor`

- **Location:** [src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor) — `.density-bar` / `.density-tick`
- **MudBlazor component replaced:** *None applicable.*
- **Reason:** This is a dataviz primitive — variable-height bars where the height is proportional to the event count for that year (`Math.Log(count)`). It is not a chip or button in a semantic sense, so there is no MudBlazor equivalent to deviate from. Listed here only to pre-empt the question.
- **Revisit when:** N/A.

### Lens filter chips — `GalaxyMapUnified.razor`

- **Location:** [src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor](../../src/StarWarsData.Frontend/Components/Pages/GalaxyMapUnified.razor) — `.lens-chip` CSS class
- **MudBlazor component replaced:** `MudChip<T>`
- **Reason:** Same sizing constraint as the era chip bar — this row sits alongside the era chips in the same compact toolbar and needs matching height/padding for visual alignment.
- **Revisit when:** Same as era chip bar. If one is converted, the other should be converted at the same time.

## Consequences

- A new contributor reading the frontend code will see a mix of `<MudChip>` and raw `<button>` elements; this ADR is the canonical explanation.
- Theme changes in MudBlazor (e.g. border radius, primary colour) propagate automatically to deviating elements because they reference `var(--mud-palette-*)` CSS variables. Do not hardcode colours in deviation CSS — always reference the MudBlazor theme variables.
- When MudBlazor is upgraded, review the "Revisit when" conditions for each entry in the catalogue to see if the deviation can be removed.
