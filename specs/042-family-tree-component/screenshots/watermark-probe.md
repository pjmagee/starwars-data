# Watermark / branding probe — family-chart-premium 0.0.0-beta.2

**Date**: 2026-05-24
**Build**: family-chart-premium 0.0.0-beta.2 (npm tarball shasum `494492bc21c9c6ef3da631b05aebcc1e404dee35`)
**Research reference**: [research.md R-3](../research.md#r-3-watermark--license-key-behaviour-in-the-free-tier)

## Finding

**Watermark IS present** in the free build:

- Position: bottom-right corner of the rendered chart canvas
- Text: **"Family Chart (Free)"** (small, semi-transparent)
- Mechanism: drawn by the bundle itself when `setLicenseKey()` has not been called
- Affected files: none — the watermark is part of the rendered SVG/HTML the library produces, not a CSS layer we can suppress

## Decision

Watermark is **kept as-is** per Principle I and upstream licence restriction #2
("You may not circumvent, disable, or remove the license key validation or
watermark features.") The library is being used inside the free tier for an
independent, non-commercial fan project, so the licence terms apply and the
watermark stays.

If the project ever takes commercial activity, the remediation path is one of:

1. Purchase a commercial licence key from `donatso.dev@gmail.com` and call
   `chart.setLicenseKey('FC-XXXX-XXXX-XXXX-XXXX')` inside `family-tree.js`.
2. Migrate to MIT [donatso/family-chart](https://github.com/donatso/family-chart)
   per [quickstart.md § 7](../quickstart.md#7-migration-path-back-to-mit-family-chart)
   and re-open the two Open Questions in [spec.md](../spec.md).

## Other branding noticed

- No "Powered by …" footer outside the rendered chart.
- No license-key modal / overlay on first render.
- No telemetry calls observed in network log (only requests on the page were
  to `localhost` + the d3 CDN script + Google Fonts CSS).

## Evidence

See [skywalker-tree-desktop.png](./skywalker-tree-desktop.png) — watermark
text visible in the lower-right of the chart canvas. Also captured in the
DOM snapshot as `StaticText "Family Chart (Free)"`.
