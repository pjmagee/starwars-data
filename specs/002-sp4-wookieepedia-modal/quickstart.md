# Quickstart: SP-4 Wookieepedia Article Modal

**Date**: 2026-05-23

**Plan**: [plan.md](./plan.md)

How to validate the feature locally. Maps to acceptance scenarios in [spec.md](./spec.md) and satisfies the mandatory Chrome DevTools MCP validation gate per [Principle IV](../../.specify/memory/constitution.md#iv-ui-changes-validated-in-a-browser-non-negotiable).

---

## Prereqs

- .NET 10 SDK (per `global.json`)
- Docker (for the dev MongoDB only — unaffected by this feature, but the AppHost expects it on the default path)
- `STARWARS_OPENAI_KEY` set on the host (the agent needs OpenAI to recognise the user's request as a tool call)
- `MDB_MCP_CONNECTION_STRING` set on the host (if the agent is configured to use the MongoDB MCP for entity lookups)

If you're starting fresh: follow [ONBOARDING.md](../../ONBOARDING.md) for the snapshot-restore path so you have realistic data to ask the agent about.

---

## Start the app

The AppHost is the right entry point — it brings up the Frontend, ApiService, Admin, and the MongoDB MCP sidecar together.

```bash
# Foreground (you'll watch the logs)
dotnet run --project src/StarWarsData.AppHost

# Or with hot reload
dotnet watch --project src/StarWarsData.AppHost
```

If you're an agent (background, `/loop`, worktree), use the isolated mode so you don't collide with the developer's running AppHost:

```bash
aspire run --isolated --detach --apphost src/StarWarsData.AppHost/StarWarsData.AppHost.csproj
# tear down later:
aspire stop <id>
```

Open the Aspire dashboard URL printed at startup and click through to the Frontend resource.

---

## Pre-commit gate (unit tests)

Before doing anything in the browser, the deterministic surface MUST pass:

```bash
dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"
```

Expected: `WookieepediaUrlBuilderTests` and any modal-service unit tests run green. Targeted run:

```bash
dotnet test --project src/StarWarsData.Tests --filter "FullyQualifiedName~WookieepediaUrlBuilderTests"
```

---

## Chrome DevTools MCP validation — five exercises

These map 1:1 to the acceptance scenarios in [spec.md](./spec.md). Each MUST be executed against the running AppHost via `mcp__chrome-devtools__*` before reporting the feature complete. The dev-auth bypass in [Frontend/Program.cs](../../src/StarWarsData.Frontend/Program.cs) injects a synthetic `dev` admin principal so authorization isn't a blocker.

### Exercise 1 — P1: open an article via SP-4 (galaxy map)

1. `navigate_page` → `https://localhost:<port>/galaxy-map`
2. Open the copilot sidebar (existing affordance on the page).
3. `take_snapshot` to confirm the sidebar is mounted and the "Can drive page" chip is visible. Confirm the popover lists `sp4_open_wookieepedia_article` under a global / "Always available" section.
4. `fill` the chat input with `"show me the Wookieepedia article for Coruscant"` and `press_key` Enter.
5. Wait for the tool call to fire; `wait_for` an element with role `dialog` representing the `MudDialog`.
6. `take_screenshot` of the dialog. Visual checks:
   - The article body shows headings, the infobox table, text, and images for **Coruscant**.
   - **No** Fandom left navigation, **no** top page header, **no** ads, **no** comments, **no** footer rails.
   - The sidebar message stream shows one short SP-4 confirmation sentence.
7. `list_console_messages` — expect zero new errors. Fix any Blazor circuit drops or MudBlazor warnings before continuing.

### Exercise 2 — P2: read + click through

With the article modal from Exercise 1 still open:

1. `evaluate_script` to scroll the iframe content (or the dialog body) and confirm the modal scrolls independently of the page beneath.
2. `click` the "Open on Wookieepedia" affordance. Verify (via `list_pages` then `select_page`) that a new browser page opens at the full-chrome Wookieepedia URL for Coruscant, and the modal remains open on the previous tab.
3. Back on the original tab, `press_key` Escape. Confirm the modal closes (`take_snapshot` — no `[role=dialog]`).

### Exercise 3 — P3: continuity awareness

1. `navigate_page` → `/galaxy-map` (or any page); `take_snapshot` to find the continuity filter toggle.
2. `click` the continuity toggle until it reads **Legends**.
3. In SP-4, type `"open the article for Coruscant"`. Enter.
4. `wait_for` the dialog; `take_screenshot` and check the iframe URL via `evaluate_script` — expect `?action=render` appended to a path ending in `Coruscant/Legends`.
5. Toggle continuity to **Both**. Type `"open the Tatooine article"`.
6. `take_snapshot`: expect a "Switch to Legends" `MudButton` in the dialog actions.
7. `click` "Switch to Legends". Confirm (via `evaluate_script` on `iframe.src`) the URL flipped to `Tatooine/Legends?action=render` without a new SP-4 turn (sidebar shows no new assistant message).

### Exercise 4 — Mobile fullscreen

1. `resize_page` to 414×896 (iPhone size per Principle IV).
2. With the continuity back on Canon, type `"show me the Bothawui article"`.
3. `wait_for` the dialog. `take_screenshot`. Expect the dialog occupies the full viewport.
4. `take_snapshot` of the DOM — confirm body content is readable without horizontal scroll.

### Exercise 5 — Second-article replacement (no stacking)

Reset viewport with `resize_page` to a desktop size.

1. Type `"open the Coruscant article"`. Wait for the dialog.
2. While the dialog is open, type `"now switch to Tatooine"` (or `"show me Tatooine instead"`).
3. `take_snapshot` after the second tool call settles. Expect exactly ONE `[role=dialog]` element — the original was replaced, not stacked on top.
4. SP-4's narration should be one short sentence covering the swap, e.g. `"Switched to the Tatooine article."`

---

## Report-back template

When reporting feature completion (manual review, PR description, `/speckit-implement` summary), include:

- ✅ / ⚠ / ❌ for each of the five exercises above
- Screenshots from exercises 1, 3, 4, 5
- Any console-log warnings you saw and whether they were fixed
- The exact URL the iframe loaded (via `evaluate_script`) for at least one canon and one legends case
- A one-line note on the test suite result: `dotnet test --filter "TestCategory=Unit"` → pass/fail

If you could not run the AppHost (port collision, missing env vars), state so explicitly per Principle IV — silent omission is forbidden.
