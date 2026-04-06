using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Service for managing application-wide content filters: continuity (Canon/Legends) and realm (Star Wars/Real world)
/// </summary>
public class GlobalFilterService
{
    private Continuity? _selectedContinuity = null; // null = both
    private Realm? _selectedRealm = Realm.Starwars; // default to in-universe Star Wars content

    /// <summary>
    /// The currently selected continuity filter. Null means both Canon and Legends.
    /// </summary>
    public Continuity? SelectedContinuity => _selectedContinuity;

    /// <summary>
    /// The currently selected realm filter. Null means both Star Wars and Real.
    /// </summary>
    public Realm? SelectedRealm => _selectedRealm;

    public event Action? OnChange;

    public void SetContinuity(Continuity? continuity)
    {
        if (_selectedContinuity != continuity)
        {
            _selectedContinuity = continuity;
            OnChange?.Invoke();
        }
    }

    public void SetRealm(Realm? realm)
    {
        if (_selectedRealm != realm)
        {
            _selectedRealm = realm;
            OnChange?.Invoke();
        }
    }

    /// <summary>
    /// Gets the continuity as a query parameter string for API calls, or null if no filtering.
    /// </summary>
    public string? GetContinuityQueryParam() => _selectedContinuity?.ToString();

    /// <summary>
    /// Gets the realm as a query parameter string for API calls, or null if no filtering.
    /// </summary>
    public string? GetRealmQueryParam() => _selectedRealm?.ToString();
}
