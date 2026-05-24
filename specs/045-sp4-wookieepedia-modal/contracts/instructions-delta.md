# Contract: `CopilotAgent.InstructionsTemplate` Delta

**Date**: 2026-05-23

**Plan**: [../plan.md](../plan.md)

The text to add to [`CopilotAgent.InstructionsTemplate`](../../../src/StarWarsData.Services/AI/Agents/CopilotAgent.cs#L115). Placement: after the existing `PAGE-CONTROL TOOLS:` block, before the data-source priority section. The new block mirrors the structure of the page-control block so the model can learn one pattern.

---

## Add this block

```text
GLOBAL SP-4 TOOLS:
You have a small family of tools prefixed `sp4_*` that are AVAILABLE EVERYWHERE
the user can talk to you — they are not bound to any page. Unlike the
`<page>_*` tools which only exist when their page is focused, `sp4_*` tools are
in your catalog on every turn. Today this family contains:

- sp4_open_wookieepedia_article(pageId?, title?) — opens an in-app modal showing
  the Wookieepedia article body for a subject. Modal body is article-only (no
  Fandom site chrome). Use this when the user asks to "show", "open", "pop up",
  or "pull up" a Wookieepedia article, or expresses a desire to read the
  underlying source for an entity.

  Argument guidance:
  - Prefer `pageId` when you have one from a prior keyword_search /
    search_wiki_pages / KG lookup. It resolves deterministically.
  - Use `title` only when you don't have a pageId. Pass the canonical
    Wookieepedia title (e.g. "Coruscant", "Darth Maul", "Battle of Yavin")
    — what the user would type into Wookieepedia's search box.
  - Do NOT call this with a guessed title when you have no grounding. If the
    user mentions an entity you haven't already looked up in this conversation,
    do a quick keyword_search first to confirm the entity exists, then call
    sp4_open_wookieepedia_article with the resolved pageId.

ALWAYS narrate one short sentence after a `sp4_*` tool fires successfully,
mirroring the page-control rule. Example: "Opened the Wookieepedia article
for Coruscant." Do NOT echo the tool name or the raw return string in your
prose.

If the tool returns "Article not found for '...'", USE that signal — propose a
correction ("I couldn't find a Wookieepedia article titled 'Bothan' — did you
mean 'Bothawui'?") rather than silently re-trying or apologising.

Do NOT call sp4_open_wookieepedia_article AND a page-control navigation tool in
the same turn unless the user explicitly asked for both. The modal occludes
part of the page; opening both at once is jarring.
```

---

## Why this block, why here

- **Placement after PAGE-CONTROL TOOLS**: the model learns the two tool families together; the `sp4_*` block reads naturally as "and another family that works the same way, but always available."
- **Naming convention is taught**: the block explicitly contrasts `<page>_*` vs `sp4_*` so the agent doesn't confuse them. Design-041's existing block establishes the `<page>_*` family; the new doc graduates `sp4_*` to a peer.
- **Argument-order guidance is mandatory**: without it, models reach for `title` first because it's the human-natural field name. The narrative ordering (pageId preferred) matches Design-041 § "Prefer them over a prose link when both would work."
- **Narration rule is restated, not implied**: the existing page-tool block's narration rule applies in spirit, but restating it inside the new block prevents the model from concluding that `sp4_*` is exempt because it's a different family.
- **Anti-stacking guard**: opening the modal AND moving the map underneath in one turn is exactly the kind of viewport surprise Design-041's non-goals warn against. The block forbids it.

---

## What NOT to add

- A second copy of the FACETS / SUBJECT / CONTINUITY envelope rules — those apply unchanged and live in the existing instruction block, not in the new tool family description.
- Mode-switching guidance (Canon vs Legends choice) — handled deterministically by the URL builder from `GlobalFilterService.Continuity`. The agent does not need to think about it.
- A list of "good" example phrases for the user — discoverability is owned by the sidebar's "Can drive page" popover, not the agent prompt.

---

## Verifying after implementation

After editing `CopilotAgent.cs`, run the unit suite (`dotnet test --project src/StarWarsData.Tests --filter "TestCategory=Unit"`) to confirm any instruction-template snapshot tests (if any exist for `CopilotAgent`) accommodate the new block. Then run the five Chrome DevTools MCP exercises in [../quickstart.md](../quickstart.md) which include a smoke test that confirms the agent recognises `"show me the Coruscant article"` as a tool call.
