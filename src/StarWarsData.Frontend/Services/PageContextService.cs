namespace StarWarsData.Frontend.Services;

/// <summary>
/// What the user is currently looking at on the active page. Pages publish
/// to <see cref="PageContextService"/> on initialization and on relevant state
/// changes; the copilot sidebar reads <see cref="PageContextService.Current"/>
/// at submit time to inject context into the agent message envelope.
///
/// See Design-022 for the full per-page context shape table and the wire format.
/// </summary>
public sealed record PageContext(string Page, string? Subject = null, string? SubjectKind = null, int? SubjectId = null, IReadOnlyDictionary<string, string>? Extras = null)
{
    /// <summary>
    /// Render this context as the bracketed envelope the copilot agent expects.
    /// Format mirrors the <c>[CONTINUITY:][PREFER:]</c> envelope already used by
    /// <c>/ask</c> — the sidebar prepends this to the user's prompt.
    ///
    /// Output is empty-string when no context is published; safe to concatenate.
    /// </summary>
    public string ToEnvelope()
    {
        if (string.IsNullOrEmpty(Page))
            return "";

        var parts = new List<string>(4) { $"[PAGE: {Page}]" };

        if (!string.IsNullOrEmpty(Subject))
        {
            var kind = SubjectKind ?? "Entity";
            var id = SubjectId is int i ? $" #{i}" : "";
            parts.Add($"[SUBJECT: {kind}{id} \"{Subject}\"]");
        }

        if (Extras is { Count: > 0 })
        {
            var pairs = string.Join(";", Extras.Select(kv => $"{kv.Key}={kv.Value}"));
            parts.Add($"[FACETS: {pairs}]");
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Scoped singleton (one per Blazor circuit) holding the active page's context
/// plus any text the user has highlighted in the page. Pages call <see cref="Set"/>
/// on initialization and on relevant state changes; <see cref="Clear"/> on Dispose.
/// The copilot sidebar subscribes to <see cref="OnChange"/> to update its focus chip
/// and reads <see cref="Current"/> + <see cref="CurrentSelection"/> at message-submit time.
/// </summary>
public sealed class PageContextService
{
    public PageContext? Current { get; private set; }

    /// <summary>
    /// Text the user has currently highlighted on the page (anywhere outside the
    /// copilot drawer itself). Updated by a global selectionchange listener
    /// registered in <c>MainLayout</c>. The copilot sidebar appends this as a
    /// <c>[SELECTION: "..."]</c> envelope to the next user message.
    ///
    /// Sticky: once captured, persists until the user either makes a new
    /// non-empty selection on the page or clicks the × on the chip in the
    /// sidebar. Browser-driven selection collapses (focus shift into the chat
    /// input, click elsewhere) are deliberately ignored — the JS watcher in
    /// App.razor only forwards non-empty selections.
    /// </summary>
    public string? CurrentSelection { get; private set; }

    public event Action? OnChange;

    public void Set(PageContext context)
    {
        Current = context;
        OnChange?.Invoke();
    }

    public void Clear()
    {
        if (Current is null)
            return;
        Current = null;
        OnChange?.Invoke();
    }

    /// <summary>
    /// Update the current text-selection. Pass null/empty to clear. Caller is
    /// responsible for length-capping; service trims and stores as-is.
    /// </summary>
    public void SetSelection(string? text)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (trimmed == CurrentSelection)
            return;
        CurrentSelection = trimmed;
        OnChange?.Invoke();
    }
}
