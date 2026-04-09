using Microsoft.JSInterop;
using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Service for managing application-wide content filters: continuity (Canon/Legends) and realm (Star Wars/Real world).
/// Filter state is persisted in sessionStorage so it survives page refreshes within the same browser tab.
/// </summary>
public class GlobalFilterService(IJSRuntime js)
{
    private Continuity? _selectedContinuity = Continuity.Canon; // default Canon only (Legends OFF)
    private Realm? _selectedRealm = Realm.Starwars;
    private bool _loaded;

    public Continuity? SelectedContinuity => _selectedContinuity;
    public Realm? SelectedRealm => _selectedRealm;

    public event Action? OnChange;

    /// <summary>
    /// Load persisted filter state from sessionStorage. Call from OnAfterRenderAsync(firstRender).
    /// </summary>
    public async Task LoadFromStorageAsync()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            var continuity = await js.InvokeAsync<string?>("sessionStorage.getItem", "sw-filter-continuity");
            var realm = await js.InvokeAsync<string?>("sessionStorage.getItem", "sw-filter-realm");

            var changed = false;

            if (continuity is not null)
            {
                var parsed =
                    continuity == "" ? null
                    : Enum.TryParse<Continuity>(continuity, true, out var c) ? c
                    : (Continuity?)null;
                if (parsed != _selectedContinuity)
                {
                    _selectedContinuity = parsed;
                    changed = true;
                }
            }

            if (realm is not null)
            {
                var parsed =
                    realm == "" ? null
                    : Enum.TryParse<Realm>(realm, true, out var r) ? r
                    : (Realm?)null;
                if (parsed != _selectedRealm)
                {
                    _selectedRealm = parsed;
                    changed = true;
                }
            }

            if (changed)
                OnChange?.Invoke();
        }
        catch { }
    }

    public void SetContinuity(Continuity? continuity)
    {
        if (_selectedContinuity != continuity)
        {
            _selectedContinuity = continuity;
            PersistAsync();
            OnChange?.Invoke();
        }
    }

    public void SetRealm(Realm? realm)
    {
        if (_selectedRealm != realm)
        {
            _selectedRealm = realm;
            PersistAsync();
            OnChange?.Invoke();
        }
    }

    public string? GetContinuityQueryParam() => _selectedContinuity?.ToString();

    public string? GetRealmQueryParam() => _selectedRealm?.ToString();

    private async void PersistAsync()
    {
        try
        {
            await js.InvokeVoidAsync("sessionStorage.setItem", "sw-filter-continuity", _selectedContinuity?.ToString() ?? "");
            await js.InvokeVoidAsync("sessionStorage.setItem", "sw-filter-realm", _selectedRealm?.ToString() ?? "");
        }
        catch { }
    }
}
