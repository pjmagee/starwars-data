using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Service for managing application-wide continuity filtering
/// </summary>
public class ContinuityService
{
    private Continuity? _selectedContinuity = null; // Default to "Both" (no filtering)

    /// <summary>
    /// The currently selected continuity filter. Null means "Both" (no filtering)
    /// </summary>
    public Continuity? SelectedContinuity => _selectedContinuity;

    /// <summary>
    /// Event fired when the continuity selection changes
    /// </summary>
    public event Action? OnChange;

    /// <summary>
    /// Sets the current continuity filter
    /// </summary>
    /// <param name="continuity">The continuity to filter by, or null for "Both"</param>
    public void SetContinuity(Continuity? continuity)
    {
        if (_selectedContinuity != continuity)
        {
            _selectedContinuity = continuity;
            OnChange?.Invoke();
        }
    }

    /// <summary>
    /// Gets the continuity as a query parameter string for API calls
    /// </summary>
    /// <returns>The continuity parameter value, or null if no filtering</returns>
    public string? GetContinuityQueryParam()
    {
        return _selectedContinuity?.ToString();
    }
}
