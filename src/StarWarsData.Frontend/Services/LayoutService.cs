namespace StarWarsData.Frontend.Services;

public class LayoutService
{
    public bool IsFullscreen { get; private set; }

    /// <summary>
    /// When true, MainLayout suppresses the right-hand copilot sidebar. Pages that
    /// already host a chat surface (e.g. /ask) set this to avoid two redundant
    /// chat surfaces on screen. Mirrors <see cref="IsFullscreen"/>'s pattern —
    /// page sets in OnInitialized, clears in Dispose.
    /// </summary>
    public bool HideCopilot { get; private set; }

    public event Action? OnChange;

    public void SetFullscreen(bool value)
    {
        if (IsFullscreen != value)
        {
            IsFullscreen = value;
            OnChange?.Invoke();
        }
    }

    public void SetHideCopilot(bool value)
    {
        if (HideCopilot != value)
        {
            HideCopilot = value;
            OnChange?.Invoke();
        }
    }
}
