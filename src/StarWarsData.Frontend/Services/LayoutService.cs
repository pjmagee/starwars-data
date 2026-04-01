namespace StarWarsData.Frontend.Services;

public class LayoutService
{
    public bool IsFullscreen { get; private set; }
    public event Action? OnChange;

    public void SetFullscreen(bool value)
    {
        if (IsFullscreen != value)
        {
            IsFullscreen = value;
            OnChange?.Invoke();
        }
    }
}
