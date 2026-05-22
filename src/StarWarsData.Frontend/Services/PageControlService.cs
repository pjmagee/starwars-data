using Microsoft.Extensions.AI;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Per-circuit registry of <see cref="AIFunction"/> tools the currently focused
/// page advertises to the copilot (SP-4). The sidebar passes
/// <see cref="AvailableActions"/> as <c>ChatOptions.Tools</c> on every turn so
/// the agent can call back into the page (navigate, highlight, toggle panels…).
///
/// Pages register in <c>OnInitialized</c> and dispose the returned token in
/// <c>IDisposable.Dispose</c> / <c>IAsyncDisposable.DisposeAsync</c>. Only ONE
/// page may be registered at a time — the focused page. Re-registering while
/// another page holds the slot throws; pages that don't opt in leave the slot
/// empty and SP-4 simply has no page tools on those turns.
///
/// See Design-041 § 3 for the contract rationale.
/// </summary>
public sealed class PageControlService
{
    readonly object _gate = new();
    string? _currentPage;
    IReadOnlyList<AIFunction> _actions = [];

    public string? CurrentPage
    {
        get
        {
            lock (_gate)
                return _currentPage;
        }
    }

    public IReadOnlyList<AIFunction> AvailableActions
    {
        get
        {
            lock (_gate)
                return _actions;
        }
    }

    public event Action? OnChange;

    public IDisposable Register(string page, IReadOnlyList<AIFunction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        ArgumentNullException.ThrowIfNull(actions);

        lock (_gate)
        {
            if (_currentPage is not null)
                throw new InvalidOperationException($"PageControlService already has '{_currentPage}' registered; cannot register '{page}'. " + "A previous page failed to dispose its registration.");

            _currentPage = page;
            _actions = actions;
        }

        OnChange?.Invoke();
        return new Registration(this, page);
    }

    void Release(string page)
    {
        lock (_gate)
        {
            if (_currentPage != page)
                return; // already released or a different page now owns the slot
            _currentPage = null;
            _actions = [];
        }

        OnChange?.Invoke();
    }

    sealed class Registration(PageControlService owner, string page) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(page);
        }
    }
}
