# Contract: `sp4_open_wookieepedia_article` Tool

**Date**: 2026-05-23

**Plan**: [../plan.md](../plan.md) | **Data Model**: [../data-model.md](../data-model.md)

The `AIFunction` registered with `GlobalCopilotToolsService` that the agent calls.

---

## Identity

| Property         | Value                                                                                          |
|------------------|------------------------------------------------------------------------------------------------|
| **Tool name**    | `sp4_open_wookieepedia_article`                                                                |
| **Family**       | `sp4_*` (global SP-4 tools — see [specs/043-sp4-global-tool-family/spec.md](../../../specs/043-sp4-global-tool-family/spec.md) once authored) |
| **Registration** | `GlobalCopilotToolsService.Tools` (per-circuit; available wherever `CopilotSidebar` mounts)    |
| **Execution**    | Client-side, on the Blazor circuit, dispatched by `UseFunctionInvocation`                      |

---

## Parameter schema (model-facing)

```jsonc
{
  "type": "object",
  "properties": {
    "pageId": {
      "type": "integer",
      "description": "Internal page ID for the article (preferred). Use this when you already have it from a prior keyword_search / KG lookup result. Resolves deterministically to the correct article."
    },
    "title": {
      "type": "string",
      "description": "Article title to open (fallback when pageId is not known). Use the canonical Wookieepedia page title as you would type it into the search box, e.g. \"Coruscant\", \"Darth Maul\", \"Battle of Yavin\"."
    }
  }
  // No "required" — the delegate enforces "at least one of pageId/title" and surfaces a structured error string when both are missing.
}
```

**Description on `AIFunction`** (model-facing copy — embedded as the `description` field on `AIFunctionFactory.Create`):

> Open the Wookieepedia article for a subject in an in-app modal. The modal shows the article body only (no Fandom site chrome). Use this when the user asks to "show", "open", "pop up", or "pull up" a Wookieepedia article, or expresses a desire to read the underlying source for an entity. Prefer passing pageId (deterministic) over title (fuzzy); when you have a pageId from a prior search, use it. After this tool returns, narrate one short sentence about what you opened — do not echo the tool name or arguments.

---

## Delegate signature (C# implementation)

```csharp
// Body in WookieepediaArticleToolFactory.cs, registered via GlobalCopilotToolsService.
private async Task<string> OpenWookieepediaArticleAsync(
    int? pageId,
    string? title,
    CancellationToken cancellationToken)
{
    var requested = pageId is not null ? $"#{pageId}" : title;

    var resolved = await _modalService.OpenAsync(
        new WookieepediaArticleRequest(pageId, title?.Trim()),
        cancellationToken);

    return resolved.IsSuccess
        ? resolved.Continuity == Continuity.Legends
            ? $"Opened article: {resolved.Title} (Legends)"
            : $"Opened article: {resolved.Title}"
        : $"Article not found for '{requested}'";
}
```

Constructed via:

```csharp
var fn = AIFunctionFactory.Create(
    OpenWookieepediaArticleAsync,
    name: ToolNames.Sp4OpenWookieepediaArticle,   // const "sp4_open_wookieepedia_article"
    description: ModelFacingDescription,
    serializerOptions: jsonOptions.Value.SerializerOptions);
```

**Why pass `serializerOptions`**: per Design-041 § Resolved questions, omitting it surfaces tool-invocation failures as raw error strings that AGUI's hosting writes verbatim into the SSE wire and the client-side `AGUIChatClient` then fails to JSON-parse, killing the turn. The fix is non-optional.

---

## Return values

| Scenario                              | Return string                                              |
|---------------------------------------|------------------------------------------------------------|
| Successful open (canon variant)       | `"Opened article: Coruscant"`                              |
| Successful open (legends variant)     | `"Opened article: Coruscant (Legends)"`                    |
| pageId resolves but article missing   | `"Article not found for '#123456'"`                        |
| title cannot be resolved              | `"Article not found for 'Bothan'"`                         |
| Both pageId and title null            | `"sp4_open_wookieepedia_article requires either pageId or title."` |
| Modal service throws                  | `"Failed to open article: <short reason>"` (exceptions caught + truncated; full stack traced via the existing `IncludeDetailedErrors = true` path) |

The agent reads the return verbatim and narrates a short sentence on top of it (per `CopilotAgent` instructions). The agent MUST NOT echo the raw return as its prose — it MUST translate it (`"Opened article: Coruscant"` → `"Opened the Wookieepedia article for Coruscant."`).

---

## Idempotence

- Calling the tool twice with the same args within the same conversation MUST NOT stack modals. The second call replaces the first's content (FR-009). The return string reflects what the modal is *now* showing.
- The tool delegate is safe to invoke from any thread; the modal service marshals to the circuit synchronization context internally.
