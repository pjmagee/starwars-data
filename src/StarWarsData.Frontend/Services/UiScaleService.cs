using Microsoft.JSInterop;

namespace StarWarsData.Frontend.Services;

public class UiScaleService
{
    private const string StorageKey = "ui-scale";

    private readonly IJSRuntime _js;
    private double _scale = 1.0;

    public UiScaleService(IJSRuntime js) => _js = js;

    public double Scale => _scale;

    public static readonly (string Label, double Value)[] Presets =
    [
        ("Small", 0.85),
        ("Default", 1.0),
        ("Large", 1.15),
        ("Extra Large", 1.3),
    ];

    public event Action? OnChange;

    public async Task InitializeAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (
                double.TryParse(
                    stored,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed
                ) && parsed is >= 0.5 and <= 2.0
            )
            {
                _scale = parsed;
            }
        }
        catch
        {
            // SSR or prerender — no JS available yet
        }

        await ApplyScale();
    }

    public async Task SetScaleAsync(double scale)
    {
        _scale = Math.Clamp(scale, 0.5, 2.0);
        await ApplyScale();
        try
        {
            await _js.InvokeVoidAsync(
                "localStorage.setItem",
                StorageKey,
                _scale.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }
        catch { }

        OnChange?.Invoke();
    }

    private async Task ApplyScale()
    {
        try
        {
            var pct = _scale * 100;
            await _js.InvokeVoidAsync("swSetUiScale", pct);
        }
        catch { }
    }
}
