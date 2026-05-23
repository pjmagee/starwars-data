# Contract: Service Surface

**Date**: 2026-05-23

**Plan**: [../plan.md](../plan.md)

Public surface of the new Frontend services and component. C# signatures kept close to what the implementation will produce so `/speckit-tasks` can derive task scope without re-deciding shape.

---

## `GlobalCopilotToolsService` (scoped per circuit)

Path: `src/StarWarsData.Frontend/Services/GlobalCopilotToolsService.cs`

```csharp
using Microsoft.Extensions.AI;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Sibling of <see cref="PageControlService"/>: a per-circuit registry of SP-4
/// tools that are AVAILABLE EVERYWHERE the copilot sidebar mounts. Unlike
/// PageControlService (which is single-page, mutates on Register/Dispose),
/// GlobalCopilotToolsService is constructed once per circuit with a static
/// list of tools known at app startup.
///
/// CopilotSidebar.SubmitAsync merges Tools with PageControl.Tools before
/// populating ChatOptions.Tools so global and page tools coexist on every turn.
///
/// See Design-043 (sp4_* global tool family) for the convention. Add a tool by
/// extending the DI registration in Program.cs:
///
///   builder.Services.AddGlobalCopilotTool&lt;WookieepediaArticleToolFactory&gt;();
///
/// </summary>
public sealed class GlobalCopilotToolsService
{
    public IReadOnlyList<AIFunction> Tools { get; }
    public IReadOnlyList<PageAction> Actions { get; } // for the discoverability popover

    public GlobalCopilotToolsService(IEnumerable<IGlobalCopilotToolFactory> factories) { /* ... */ }
}

public interface IGlobalCopilotToolFactory
{
    PageAction CreateAction();
}

public static class GlobalCopilotToolsServiceCollectionExtensions
{
    public static IServiceCollection AddGlobalCopilotTool<TFactory>(this IServiceCollection services)
        where TFactory : class, IGlobalCopilotToolFactory;
}
```

**Contract notes**:

- `PageAction` is the existing record from [PageControlService](../../../src/StarWarsData.Frontend/Services/PageControlService.cs) — `(AIFunction Tool, string Label, string? Example)`. Re-used so the "Can drive page" popover renders global tools identically to page tools.
- No `Register` / no mutability. The set of global tools is fixed for a circuit's lifetime. Adding a tool requires a code change + restart.

---

## `WookieepediaArticleModalService` (scoped per circuit)

Path: `src/StarWarsData.Frontend/Services/WookieepediaArticleModalService.cs`

```csharp
namespace StarWarsData.Frontend.Services;

public sealed record WookieepediaArticleRequest(int? PageId, string? Title);

public sealed record WookieepediaArticleOpenResult(
    bool IsSuccess,
    string? Title,        // canonical title resolved (null on failure)
    Continuity Continuity // continuity used to build URL
);

public sealed class WookieepediaArticleModalService : IDisposable
{
    public Task<WookieepediaArticleOpenResult> OpenAsync(
        WookieepediaArticleRequest request,
        CancellationToken cancellationToken = default);

    public Task CloseAsync();

    // Public for the in-modal "Switch to Legends" affordance — replaces in place
    // without going through SP-4. Returns the new result for the dialog to render.
    public Task<WookieepediaArticleOpenResult> SwitchContinuityAsync(
        Continuity targetContinuity,
        CancellationToken cancellationToken = default);

    public void Dispose();
}
```

**Behaviour contract**:

- **Single-instance**: at most one `IDialogReference` held internally. A second `OpenAsync` closes the first dialog before opening the new one (FR-009 / R-007).
- **pageId resolution**: when `request.PageId is not null`, the service calls the existing entity-lookup API (`ApiClient.GetEntityByPageIdAsync(pageId)`) to obtain `(title, continuity, wikiUrl)`. The API talks to `kg.nodes` (Principle VI).
- **Title fallback**: when `request.PageId is null` but `request.Title is not null`, the title is used as-is — no validation against KG. (The agent has been told to keyword_search first.)
- **Continuity**: read from `GlobalFilterService.Continuity` at the moment `OpenAsync` is called. Snapshot semantics — does NOT subscribe to filter changes.
- **Auto-close on navigation**: subscribes to `NavigationManager.LocationChanged` once at construction; calls `CloseAsync` on every event. (FR-011.)
- **Dispose**: closes any open dialog; unsubscribes from `LocationChanged`.

---

## `WookieepediaUrlBuilder` (pure-logic static class)

Path: `src/StarWarsData.Frontend/Services/WookieepediaUrlBuilder.cs`

```csharp
namespace StarWarsData.Frontend.Services;

public static class WookieepediaUrlBuilder
{
    /// <summary>
    /// Build the iframe src URL pointing at Wookieepedia's body-only article endpoint.
    /// </summary>
    public static Uri BuildRenderUrl(string canonicalTitle, Continuity continuity);

    /// <summary>
    /// Build the canonical (full-page, with Fandom chrome) URL — used for the
    /// "Open on Wookieepedia" affordance and the modal footer link.
    /// </summary>
    public static Uri BuildCanonicalUrl(string canonicalTitle, Continuity continuity);

    /// <summary>
    /// Normalise an inbound title (trim, reject empty, replace spaces with underscores
    /// per Wookieepedia convention).
    /// </summary>
    public static string NormaliseTitle(string title);
}
```

**Behaviour contract**:

- `BuildRenderUrl("Coruscant", Continuity.Canon)` → `https://starwars.fandom.com/wiki/Coruscant?action=render`
- `BuildRenderUrl("Coruscant", Continuity.Legends)` → `https://starwars.fandom.com/wiki/Coruscant/Legends?action=render`
- `BuildRenderUrl("Coruscant", Continuity.Both)` → same as `Canon` (canon-default per R-005)
- `BuildRenderUrl("Darth Maul", _)` → spaces become underscores (Wookieepedia URL convention)
- `BuildRenderUrl("Bail Organa's Speech", _)` → apostrophe stays raw (Wookieepedia handles it); non-ASCII percent-encoded by `Uri.EscapeDataString` *on the title portion only*, never on the `/Legends` suffix
- `NormaliseTitle("  Coruscant  ")` → `"Coruscant"`
- `NormaliseTitle("")` / `NormaliseTitle("   ")` → throws `ArgumentException`

**Unit tests** (under `src/StarWarsData.Tests/Unit/Frontend/WookieepediaUrlBuilderTests.cs`):

- Canon + canonical entity → expected URL
- Legends suffix appended correctly
- Both filter → canon URL
- Spaces become underscores
- Non-ASCII title → percent-encoded
- Apostrophe preserved
- Trailing/leading whitespace trimmed
- Empty / all-whitespace title throws
- The `/Legends` literal is not double-encoded

---

## `WookieepediaArticleDialog.razor` (MudBlazor dialog component)

Path: `src/StarWarsData.Frontend/Components/Shared/WookieepediaArticleDialog.razor`

```razor
@inherits MudComponentBase

<MudDialog>
    <TitleContent>
        <MudText Typo="Typo.h6">@_articleTitle</MudText>
        @if (_continuity == Continuity.Legends)
        {
            <ContinuityBadge Continuity="Continuity.Legends" />  @* per principle VII colour rules *@
        }
    </TitleContent>

    <DialogContent>
        @if (_loading) { <MudProgressLinear Color="Color.Primary" Indeterminate="true" /> }
        @if (_error is not null) { /* error state + retry */ }
        <iframe src="@_renderUrl"
                sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
                referrerpolicy="no-referrer"
                loading="lazy"
                title="Wookieepedia article: @_articleTitle"
                style="width:100%; height:70vh; border:0;"
                @onload="OnIframeLoaded" />
    </DialogContent>

    <DialogActions>
        @if (_continuity == Continuity.Canon && _globalFilter.Continuity == Continuity.Both)
        {
            <MudButton OnClick="SwitchToLegends">Switch to Legends</MudButton>
        }
        <MudLink Href="@_canonicalUrl" Target="_blank" Underline="Underline.Hover">
            Open on Wookieepedia
        </MudLink>
        <MudButton OnClick="Close" Color="Color.Default">Close</MudButton>
    </DialogActions>
</MudDialog>
```

**Parameters** (passed via `DialogParameters` on `IDialogService.ShowAsync`):

| Parameter        | Type        | Description                                                                    |
|------------------|-------------|--------------------------------------------------------------------------------|
| `ArticleTitle`   | `string`    | Canonical title to display in the header                                       |
| `RenderUrl`      | `Uri`       | iframe `src` URL (already includes `?action=render`)                           |
| `CanonicalUrl`   | `Uri`       | URL for the "Open on Wookieepedia" affordance (without `?action=render`)       |
| `Continuity`     | `Continuity`| Continuity used for this open (drives the badge + switch-affordance visibility) |

**Options** (`DialogOptions`):

- `MaxWidth = MaxWidth.Large`
- `FullWidth = true`
- `FullScreen = false` on desktop; component uses a CSS media query (`@media (max-width: 960px)`) on its scoped `.razor.css` to expand to full viewport — matches the project's existing mobile breakpoint conventions per [CLAUDE.md](../../../CLAUDE.md) "Mobile" section.
- `CloseButton = true`, `CloseOnEscapeKey = true`, `BackdropClick = true`

---

## DI registration (Program.cs)

Add to `src/StarWarsData.Frontend/Program.cs`, alongside the existing `PageControlService` registration:

```csharp
builder.Services.AddScoped<PageControlService>();
builder.Services.AddScoped<GlobalCopilotToolsService>();
builder.Services.AddScoped<WookieepediaArticleModalService>();
builder.Services.AddGlobalCopilotTool<WookieepediaArticleToolFactory>();
```

`WookieepediaArticleToolFactory` implements `IGlobalCopilotToolFactory` and creates the `AIFunction` documented in [tool-contract.md](./tool-contract.md). It takes `WookieepediaArticleModalService` + `IOptions<JsonOptions>` from DI.
