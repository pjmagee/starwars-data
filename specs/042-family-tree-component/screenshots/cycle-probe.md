# Cycle probe — research.md R-6

**Date**: 2026-05-24
**Prompt**: "Show me Boba Fett's family tree"
**Tool selected by agent**: `render_family_tree` (correct routing)
**Build**: family-chart-premium 0.0.0-beta.2

## Result

**No cycle failure. Tree renders cleanly.**

- Boba Fett (canon) appears centered with Jango Fett above as the "parent"
  (modelling the canon clone-of-Jango lineage as a normal parent_of edge).
- No infinite loop, no JS stack overflow, no DOM thrashing.
- Console: zero errors after render.
- Layout: family-chart's tree fit the entire visible chart canvas at 1440×900.

The clone-as-parent relationship is treated by family-chart as a normal
parent/child link — there is no "this person is their own ancestor" cycle in
the underlying `kg.edges` data, so the renderer is comfortable. The
documented fallback (`limitations.cycleFallback=true` plus `render_graph`
Tree mode) was **not** triggered. R-6 stays as "trust the renderer for v1"
with the fallback path available if a future query exposes a true cycle.

## Limitations panel

The advisory chip showed: *"Missing gender on 3 characters · 21 adoptive
relations excluded"*. Both numbers are reasonable for the Fett canon BFS;
neither indicates a renderer failure.

## Evidence

See [boba-fett-tree.png](./boba-fett-tree.png).
