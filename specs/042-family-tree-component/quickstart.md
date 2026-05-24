# Quickstart — Family Tree Component

**Branch**: `feature/042-family-tree-component` | **Plan**: [plan.md](./plan.md)

Dev-verification recipes for the family tree feature. Covers vendoring the JS bundle, exercising the endpoint, and the Chrome DevTools MCP UI validation required by Principle IV.

---

## 0. Prerequisites

- The branch `feature/042-family-tree-component` is checked out.
- `starwars-dev` has a populated KG (`raw.pages.infobox` + `kg.nodes` + `kg.edges`) — the [ONBOARDING.md](../../ONBOARDING.md) snapshot restore is the cheapest way to get there.
- AppHost can start under your local Aspire CLI: `aspire run --isolated --detach --project src/StarWarsData.AppHost`.

---

## 1. Vendor family-chart-premium

One-time setup. The dist isn't on npm — it's the `dist/` directory of the GitHub repo's `main` branch.

```pwsh
$repo = "donatso/family-chart-premium"
$sha  = (gh api "repos/$repo/commits/main" --jq .sha)
$dest = "src/StarWarsData.Frontend/wwwroot/lib/family-chart-premium"
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# Fetch the bundle + styles + LICENSE
gh api "repos/$repo/contents/dist/family-chart.js?ref=$sha" --jq .content `
  | [Convert]::FromBase64String((& { $input }) -join '') `
  | Set-Content -Path "$dest/family-chart.js" -Encoding Byte

# Fetch the styles directory (loop because gh api returns one file at a time)
$styleFiles = gh api "repos/$repo/contents/dist/styles?ref=$sha" --jq '.[].name'
foreach ($f in ($styleFiles -split "`n")) {
  gh api "repos/$repo/contents/dist/styles/$($f)?ref=$sha" --jq .content `
    | [Convert]::FromBase64String((& { $input }) -join '') `
    | Set-Content -Path "$dest/styles/$f" -Encoding Byte
}

gh api "repos/$repo/contents/LICENSE.txt?ref=$sha" --jq .content `
  | [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String((& { $input }) -join '')) `
  | Set-Content -Path "$dest/LICENSE.txt"

Set-Content -Path "$dest/VERSION.txt" -Value $sha
```

After running:

- `dest/family-chart.js` is the UMD bundle.
- `dest/styles/*` is the CSS.
- `dest/LICENSE.txt` is the upstream notice verbatim (Principle I — required by restriction #3 of the licence).
- `dest/VERSION.txt` is the upstream commit SHA we pinned to.

Then add this to the `<head>` block of `App.razor`, alongside the existing d3 reference:

```html
<link rel="stylesheet" href="lib/family-chart-premium/styles/family-chart.css" />
<script src="lib/family-chart-premium/family-chart.js"></script>
```

---

## 2. Start the AppHost

```pwsh
aspire run --isolated --detach --project src/StarWarsData.AppHost
```

Once Aspire reports the frontend resource is Running:

```pwsh
mcp__aspire__list_resources | Select-String "frontend"
```

Note the `https` URL (e.g. `https://localhost:57287`).

---

## 3. Smoke the endpoint directly (Phase 1 done)

```pwsh
$base = "https://localhost:57284"  # ApiService https URL from list_resources
$anakin = 452390  # PageId of Anakin Skywalker in starwars-dev

# Headers: X-User-Id is required (ADR-001). For local dev, the synthetic 'dev' principal
# from the Frontend isn't on the wire — call the API with a fixed dev user header instead.
curl -k "$base/api/RelationshipGraph/family-tree/$anakin?maxDepth=3&continuity=Canon" `
  -H "X-User-Id: dev" `
  | ConvertFrom-Json `
  | ConvertTo-Json -Depth 8
```

Verify:

- HTTP `200`.
- `rootId == "452390"`.
- `people` contains entries for Anakin, Shmi, Padmé, Luke, Leia.
- Every `rels.spouses[]` reference is bidirectional.
- `limitations.missingGenders` is `[]` (Anakin/Shmi/Padmé/Luke/Leia all have Gender in their infoboxes).
- `kinship` is `null` or `[]` (no `has_relative` edges in the Skywalker seed).

Run the bidirectional-link assertion explicitly:

```pwsh
$r = curl -k "$base/api/RelationshipGraph/family-tree/$anakin" -H "X-User-Id: dev" | ConvertFrom-Json
$people = @{}; foreach ($p in $r.people) { $people[$p.id] = $p }
$broken = @()
foreach ($p in $r.people) {
  foreach ($s in ($p.rels.spouses ?? @())) {
    if ($people[$s].rels.spouses -notcontains $p.id) {
      $broken += "$($p.id) -> $s (spouse not bidirectional)"
    }
  }
}
$broken | Format-List
```

Expected: empty list.

---

## 4. Run the unit + integration tier

```pwsh
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit|FullyQualifiedName~FamilyTree"
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Integration&FullyQualifiedName~FamilyTree"
```

Both MUST pass before report-back.

---

## 5. Chrome DevTools MCP UI validation (Principle IV — NON-NEGOTIABLE)

Required validation loop for any UI-touching change. Capture the screenshots under `specs/042-family-tree-component/screenshots/` for the PR.

### 5.1 Navigate and trigger the agent

```text
mcp__chrome-devtools__navigate_page → https://localhost:57287/ask
mcp__chrome-devtools__wait_for → ["Ask"]
mcp__chrome-devtools__fill → uid of the chat textarea, value:
  "Show me the Skywalker family tree centered on Anakin Skywalker"
mcp__chrome-devtools__click → uid of the submit button
mcp__chrome-devtools__wait_for → ["Anakin", "Padmé", "Luke", "Leia"]
```

### 5.2 Snapshot the rendered tree

```text
mcp__chrome-devtools__take_snapshot
mcp__chrome-devtools__take_screenshot → save to screenshots/skywalker-tree-desktop.png
```

Confirm in the snapshot:

- The `MudPaper` wrapper exists with the title chip "Skywalker family tree centered on Anakin Skywalker".
- The `<div id="family-tree-*" class="f3">` chart mount is populated (the snapshot shows person cards for Anakin, Padmé, Luke, Leia, Shmi).
- The `limitations` advisory chip is absent (no orange MudAlert).
- A continuity badge appears in `Color.Primary` (Canon — Principle VII canonical colours).

### 5.3 Mobile fallback check

```text
mcp__chrome-devtools__resize_page → width: 414, height: 896
mcp__chrome-devtools__take_snapshot
mcp__chrome-devtools__take_screenshot → save to screenshots/skywalker-tree-mobile.png
```

Confirm:

- The chart `<div>` is hidden (`d-md-block`).
- The `MobileSummary` markdown bullet list is rendered (`d-block d-md-none`).
- No JS console errors from the f3 chart trying to lay out at 0-width.

### 5.4 Console + network audit

```text
mcp__chrome-devtools__list_console_messages → types: ["error", "warn"]
mcp__chrome-devtools__list_network_requests → resourceTypes: ["fetch"]
```

Confirm:

- No Blazor circuit drops.
- No `f3.createChart` errors.
- No 4xx/5xx on `/api/RelationshipGraph/family-tree/452390`.
- One successful `GET /api/citations/resolve` (post-render citation resolution).

### 5.5 Watermark / branding probe (research.md R-3)

Visually inspect the rendered chart for:

- Any "Powered by family-chart-premium" footer.
- Any watermark on the cards or the chart background.
- Any modal / overlay asking for a license key.

Record findings in `screenshots/watermark-probe.md`. If anything surfaces, accept it (do NOT patch the bundle); decide with the user whether to keep it or migrate to MIT family-chart (see [spec.md § Revisit when](./spec.md#revisit-when)).

### 5.6 Cycle probe (research.md R-6)

```text
mcp__chrome-devtools__navigate_page → https://localhost:57287/ask
fill the textarea with "Show me Boba Fett's family tree" → submit
mcp__chrome-devtools__wait_for → ["Boba Fett", "Jango Fett"]
mcp__chrome-devtools__take_screenshot → save to screenshots/boba-fett-tree.png
mcp__chrome-devtools__list_console_messages → types: ["error"]
```

Confirm:

- The clone relationship renders (Boba is Jango's "son" / clone in canon).
- No infinite-loop error or JS stack overflow.
- The tree shows expected nodes without visual breakage.

---

## 6. Update vendored bundle

When upstream cuts a new commit you want to pin to:

```pwsh
# Repeat step 1 with the new SHA. Then re-run all of step 5 to re-verify behaviour.
# If anything visually changes, capture before/after screenshots in the PR.
```

When upstream cuts a stable release (non-`0.0.0-beta.x`):

- Update `VERSION.txt` to the semver tag (e.g. `1.0.0`).
- Update spec.md § Library choice's "Version pin" line.
- Run step 5 against the new build before merging.

---

## 7. Migration path back to MIT family-chart

If the project ever takes on commercial activity (per [spec.md § Revisit when](./spec.md#revisit-when)):

1. `rm -r src/StarWarsData.Frontend/wwwroot/lib/family-chart-premium/`
2. Vendor [donatso/family-chart](https://github.com/donatso/family-chart) under `wwwroot/lib/family-chart/` instead.
3. Update the `<script>`/`<link>` paths in `App.razor`.
4. Remove the `f3Chart.plugin(f3.plugins.spouseLinkText(...))` and `f3.plugins.kinship(...)` calls from `wwwroot/js/family-tree.js` (those plugins are premium-only).
5. Re-open the two Open Questions in spec.md (partner-vs-spouse distinction, `has_relative` ambiguity) and decide how to handle them in the MIT-only world.
6. Re-run the entire step 5 verification loop.

This is documented as a fire-drill, not a planned activity.
