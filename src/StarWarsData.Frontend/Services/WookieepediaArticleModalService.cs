using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using MudBlazor;
using StarWarsData.Frontend.Components.Shared;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Models.Wookieepedia;

namespace StarWarsData.Frontend.Services;

public sealed class WookieepediaArticleModalService : IDisposable
{
    readonly IDialogService _dialogService;
    readonly NavigationManager _navigationManager;
    readonly IHttpClientFactory _httpClientFactory;
    readonly GlobalFilterService _globalFilter;
    readonly ILogger<WookieepediaArticleModalService> _logger;

    IDialogReference? _current;
    string? _currentTitle;
    Continuity _currentContinuity = Continuity.Canon;

    public WookieepediaArticleModalService(
        IDialogService dialogService,
        NavigationManager navigationManager,
        IHttpClientFactory httpClientFactory,
        GlobalFilterService globalFilter,
        ILogger<WookieepediaArticleModalService> logger
    )
    {
        _dialogService = dialogService;
        _navigationManager = navigationManager;
        _httpClientFactory = httpClientFactory;
        _globalFilter = globalFilter;
        _logger = logger;
        _navigationManager.LocationChanged += OnLocationChanged;
    }

    public async Task<WookieepediaArticleOpenResult> OpenAsync(WookieepediaArticleRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PageId is null && string.IsNullOrWhiteSpace(request.Title))
            return new WookieepediaArticleOpenResult(false, null, Continuity.Canon);

        string? title = request.Title?.Trim();

        if (request.PageId is { } pageId)
        {
            var resolved = await ResolveTitleAsync(pageId, cancellationToken);
            if (resolved is null)
                return new WookieepediaArticleOpenResult(false, null, Continuity.Canon);
            title = resolved;
        }

        if (string.IsNullOrWhiteSpace(title))
            return new WookieepediaArticleOpenResult(false, null, Continuity.Canon);

        // Snapshot continuity at open-time per research.md § R-005 — Canon/Legends pick
        // the suffix; Both/Unknown/null default to Canon (the dialog surfaces a "Switch
        // to Legends" affordance when the global filter is Both).
        var continuity = ResolveContinuity(_globalFilter.SelectedContinuity);
        return await ShowDialogAsync(title, continuity);
    }

    static Continuity ResolveContinuity(Continuity? filterContinuity) => filterContinuity == Continuity.Legends ? Continuity.Legends : Continuity.Canon;

    async Task<WookieepediaArticleOpenResult> ShowDialogAsync(string title, Continuity continuity)
    {
        var renderUrl = WookieepediaUrlBuilder.BuildRenderUrl(title, continuity);
        var canonicalUrl = WookieepediaUrlBuilder.BuildCanonicalUrl(title, continuity);

        if (_current is not null)
            _current.Close();

        var parameters = new DialogParameters<WookieepediaArticleDialog>
        {
            { x => x.ArticleTitle, title },
            { x => x.RenderUrl, renderUrl },
            { x => x.CanonicalUrl, canonicalUrl },
            { x => x.Continuity, continuity },
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.ExtraLarge,
            FullWidth = true,
            CloseButton = true,
            CloseOnEscapeKey = true,
            BackdropClick = true,
        };

        _current = await _dialogService.ShowAsync<WookieepediaArticleDialog>(title, parameters, options);
        _currentTitle = title;
        _currentContinuity = continuity;

        return new WookieepediaArticleOpenResult(true, title, continuity);
    }

    /// <summary>
    /// Reopen the current article with a different continuity variant. Used by the
    /// "Switch to Legends" affordance in the dialog. Does NOT call SP-4 — this is a
    /// direct UI action.
    /// </summary>
    public Task<WookieepediaArticleOpenResult> SwitchContinuityAsync(Continuity targetContinuity, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // reserved for future async work
        if (_currentTitle is null)
            return Task.FromResult(new WookieepediaArticleOpenResult(false, null, Continuity.Canon));
        return ShowDialogAsync(_currentTitle, targetContinuity);
    }

    public Task CloseAsync()
    {
        _current?.Close();
        _current = null;
        _currentTitle = null;
        return Task.CompletedTask;
    }

    async Task<string?> ResolveTitleAsync(int pageId, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("StarWarsData");
            using var response = await http.PostAsJsonAsync("api/citations/resolve", new { pageIds = new[] { pageId } }, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var refs = await response.Content.ReadFromJsonAsync<List<CitationReference>>(cancellationToken: ct);
            return refs?.FirstOrDefault()?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve pageId {PageId} for Wookieepedia modal", pageId);
            return null;
        }
    }

    void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_current is not null)
        {
            _current.Close();
            _current = null;
            _currentTitle = null;
        }
    }

    public void Dispose()
    {
        _navigationManager.LocationChanged -= OnLocationChanged;
        _current?.Close();
        _current = null;
    }
}
