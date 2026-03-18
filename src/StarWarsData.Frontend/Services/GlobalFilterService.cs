using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Service for managing application-wide content filters: continuity (Canon/Legends) and universe (In/Out of universe)
/// </summary>
public class GlobalFilterService
{
    private Continuity? _selectedContinuity = null; // null = both
    private Universe? _selectedUniverse = Universe.InUniverse; // default to in-universe only

    /// <summary>
    /// The currently selected continuity filter. Null means both Canon and Legends.
    /// </summary>
    public Continuity? SelectedContinuity => _selectedContinuity;

    /// <summary>
    /// The currently selected universe filter. Null means both in-universe and out-of-universe.
    /// </summary>
    public Universe? SelectedUniverse => _selectedUniverse;

    public event Action? OnChange;

    public void SetContinuity(Continuity? continuity)
    {
        if (_selectedContinuity != continuity)
        {
            _selectedContinuity = continuity;
            OnChange?.Invoke();
        }
    }

    public void SetUniverse(Universe? universe)
    {
        if (_selectedUniverse != universe)
        {
            _selectedUniverse = universe;
            OnChange?.Invoke();
        }
    }

    /// <summary>
    /// Gets the continuity as a query parameter string for API calls, or null if no filtering.
    /// </summary>
    public string? GetContinuityQueryParam() => _selectedContinuity?.ToString();

    /// <summary>
    /// Gets the universe as a query parameter string for API calls, or null if no filtering.
    /// </summary>
    public string? GetUniverseQueryParam() => _selectedUniverse?.ToString();
}
