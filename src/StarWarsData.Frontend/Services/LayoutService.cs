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

    /// <summary>
    /// Whether the right-hand SP-4 drawer is open. Single source of truth so
    /// any page (e.g. the Getting Started callout) can open/close the same
    /// panel the app-bar toggle controls. Open by default. Honoured only when
    /// <see cref="HideCopilot"/> is false.
    /// </summary>
    public bool CopilotOpen { get; private set; } = true;

    public event Action? OnChange;

    public void SetCopilotOpen(bool value)
    {
        if (CopilotOpen != value)
        {
            CopilotOpen = value;
            OnChange?.Invoke();
        }
    }

    public void ToggleCopilot() => SetCopilotOpen(!CopilotOpen);

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
