# Phase 1 Data Model: SP-4 Wookieepedia Article Modal

**Date**: 2026-05-23

**Plan**: [plan.md](./plan.md) | **Research**: [research.md](./research.md)

This feature has **no persisted data**. Nothing lands in MongoDB, nothing is cached on disk, no migration runs. The "data model" below describes the transient in-memory shapes the new code uses; durable model contracts (KG nodes, edges, etc.) are unchanged.

---

## Entities

### `WookieepediaArticleRequest` (tool input DTO)

The shape the agent emits as the `sp4_open_wookieepedia_article` tool's arguments, deserialized by `UseFunctionInvocation` into the delegate's parameters.

| Field      | Type      | Required           | Description                                                                          |
|------------|-----------|--------------------|--------------------------------------------------------------------------------------|
| `pageId`   | `int?`    | One of pageId/title MUST be set | Internal KG `pageId`. When present, the modal service resolves it to a canonical title via the existing entity-lookup API. Preferred over `title`. |
| `title`    | `string?` | One of pageId/title MUST be set | Free-text article title. Used directly in the URL builder when `pageId` is absent. Fallback path when the agent doesn't have a pageId in context. |

**Validation rules** (enforced by the tool delegate, not by `JsonConverter`s):

- `pageId` and `title` MUST NOT both be null. If both are null, the tool returns the failure string `"sp4_open_wookieepedia_article requires either pageId or title."`.
- `pageId` and `title` MAY both be set — the resolver uses pageId and ignores title in that case (deterministic precedence).
- `title` MUST be trimmed and non-empty after trimming; an all-whitespace title is treated as null.

**Notes**:

- This is **not** the same shape as the `Reference` type in `Models/AI/Ask.cs`. The Reference converter (Design-041 § Resolved questions) handles arbitrary bare-int/bare-URL forms emitted by render-tools; `WookieepediaArticleRequest` is a discrete two-field DTO with no ambiguity, so the standard `JsonStringEnumConverter`-free path is sufficient.
- Agents see the description (model-facing copy) on `AIFunction.Description`; the spec for that copy lives in [contracts/tool-contract.md](./contracts/tool-contract.md).

---

### `WookieepediaArticleResult` (tool return value)

The string the tool delegate returns to `UseFunctionInvocation`, which the middleware appends to the conversation as a `role:tool` `ChatMessage` and the agent reads in the next turn to produce its confirmation narration.

| Field   | Type            | Description |
|---------|-----------------|-------------|
| (value) | `string`        | A short status string. Successful open: `"Opened article: <canonical title>"` (e.g. `"Opened article: Coruscant"`). Continuity-resolved successful open: `"Opened article: Coruscant (Legends)"`. Failure (title not resolved): `"Article not found for '<requested name>'"`. Failure (no args): `"sp4_open_wookieepedia_article requires either pageId or title."`. |

**Why string, not a typed object**:

- The agent reads this verbatim and uses it to inform its narration; a structured JSON return would just be serialized to a string for the model anyway.
- Matches the pattern of the existing page-control tools (Design-041 § 3) — string return, agent narrates.
- A future iteration that needs richer signal (e.g. "Opened article, but the page is a redirect to X") can introduce a structured return without changing the wire contract — `UseFunctionInvocation` serializes whatever the delegate returns.

---

### `ModalSession` (in-memory service state)

Held privately by `WookieepediaArticleModalService` (scoped per Blazor circuit). Not a public type.

| Field                  | Type                              | Description |
|------------------------|-----------------------------------|-------------|
| `current`              | `IDialogReference?`               | The currently open article modal, if any. `null` means closed. |
| `currentArticleTitle`  | `string?`                         | Canonical title of the currently-displayed article. Used by the in-modal "Switch to Legends" affordance to re-open with the same title. |
| `currentContinuity`    | `Continuity`                      | Continuity used to build the current URL (Canon / Legends). |

**State transitions**:

```text
[Closed] --OpenAsync(req)--> [Opening] --(dialog visible, iframe loading)--> [Ready]

[Ready] --OpenAsync(req)--> [Replacing] --(close current, open new)--> [Ready]

[Ready] --CloseAsync() / dialog dismissed / NavigationManager.LocationChanged--> [Closed]

[Ready] --user clicks "Switch to Legends" affordance--> [Replacing] --> [Ready]
        (note: this path does NOT call SP-4; it replaces in-place)
```

**Invariants**:

- At any moment, at most one article modal is open per circuit (FR-009).
- A circuit's modal closes when the circuit disposes (page reload, tab close, user logs out) — handled by `IDisposable` on the scoped service.

---

### `Continuity` (existing enum, referenced)

The feature consumes the existing `Continuity` enum from the global filter:

| Value     | Meaning            | Maps to URL behaviour                              |
|-----------|--------------------|----------------------------------------------------|
| `Canon`   | Canon only         | No suffix                                          |
| `Legends` | Legends only       | `/Legends` suffix                                  |
| `Both`    | Both visible       | No suffix (canon default); "Switch to Legends" affordance shown |

No changes to this enum.

---

### `GlobalFilterService` (existing service, referenced)

Read-only consumer. The modal service subscribes only to know whether the host has navigated (which triggers close, R-005 / spec edge case). It does NOT re-render on filter change — that's deliberate (spec edge case).

No changes to this service.

---

## Relationships

```text
GlobalCopilotToolsService           PageControlService
        |                                   |
        +--- AIFunction(s) ---+---+---------+
                              |
                              v
                       ChatOptions.Tools
                              |
                              v
                  CopilotSidebar.SubmitAsync
                              |
                              v
                     AGUIChatClient + UseFunctionInvocation
                              |
                              | (on sp4_open_wookieepedia_article call)
                              v
              WookieepediaArticleToolFactory (the AIFunction's body)
                              |
                              v
              WookieepediaArticleModalService.OpenAsync(req)
                              |
              +---------------+---------------+
              v                               v
      WookieepediaUrlBuilder         GlobalFilterService.Continuity
              |                               |
              +-----------+-------------------+
                          v
                  iframe src URL
                          |
                          v
                  IDialogService.ShowAsync<WookieepediaArticleDialog>
```

---

## Persistence

**None.** This feature writes nothing to MongoDB, nothing to localStorage, nothing to the user's profile. Modal state is purely per-circuit and disposes with the circuit.

Refresh = clean slate (consistent with the existing filter-state behaviour documented in Design-041 § Open questions).
