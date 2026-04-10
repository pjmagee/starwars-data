using MudBlazor;

namespace StarWarsData.Frontend.Theming;

/// <summary>
/// Static palette definitions for the six Star Wars faction themes.
/// Each <see cref="SwTheme"/> resolves to a (PaletteLight, PaletteDark) pair via
/// <see cref="GetPalettes"/>. MudBlazor's theming pipeline auto-generates the
/// CSS variables (--mud-palette-*) from whichever palette is active and the
/// IsDarkMode flag, so all components and utility classes pick up the new colours
/// automatically when the user switches themes — no per-component plumbing.
/// </summary>
public static class Themes
{
    public static (PaletteLight Light, PaletteDark Dark) GetPalettes(SwTheme theme) =>
        theme switch
        {
            SwTheme.RebelAlliance => (RebelAllianceLight, RebelAllianceDark),
            SwTheme.GalacticEmpire => (GalacticEmpireLight, GalacticEmpireDark),
            SwTheme.JediOrder => (JediOrderLight, JediOrderDark),
            SwTheme.SithOrder => (SithOrderLight, SithOrderDark),
            SwTheme.MandalorianClans => (MandalorianClansLight, MandalorianClansDark),
            _ => (HolonetLight, HolonetDark),
        };

    // ── Holonet (default) — current violet/cyan tech aesthetic ──

    public static readonly PaletteLight HolonetLight = new()
    {
        Primary = "#7e6fff",
        Secondary = "#ff4081",
        Tertiary = "#4a86ff",
        AppbarText = "#424242",
        AppbarBackground = "rgba(255,255,255,0.85)",
        DrawerBackground = "#ffffff",
        Background = "#fafafa",
        Surface = "#ffffff",
        GrayLight = "#e8e8e8",
        GrayLighter = "#f9f9f9",
    };

    public static readonly PaletteDark HolonetDark = new()
    {
        Primary = "#7e6fff",
        Secondary = "#ff4081",
        Tertiary = "#4a86ff",
        Surface = "#1e1e2d",
        Background = "#1a1a27",
        BackgroundGray = "#151521",
        AppbarText = "#92929f",
        AppbarBackground = "rgba(26,26,39,0.85)",
        DrawerBackground = "#1a1a27",
        ActionDefault = "#74718e",
        ActionDisabled = "#9999994d",
        ActionDisabledBackground = "#605f6d4d",
        TextPrimary = "#b2b0bf",
        TextSecondary = "#92929f",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#92929f",
        DrawerText = "#92929f",
        GrayLight = "#2a2833",
        GrayLighter = "#1e1e2d",
        Info = "#4a86ff",
        Success = "#3dcb6c",
        Warning = "#ffb545",
        Error = "#ff3f5f",
        LinesDefault = "#33323e",
        TableLines = "#33323e",
        Divider = "#292838",
        OverlayLight = "#1e1e2d80",
    };

    // ── Rebel Alliance — phoenix orange + worn metal + warm cream ──

    public static readonly PaletteLight RebelAllianceLight = new()
    {
        Primary = "#ff7a18",
        Secondary = "#c89630",
        Tertiary = "#4a4a4a",
        AppbarText = "#3a2010",
        AppbarBackground = "rgba(255,250,245,0.9)",
        DrawerBackground = "#fff8ee",
        Background = "#fdf6ed",
        Surface = "#ffffff",
        TextPrimary = "#2a1a0a",
        TextSecondary = "#5a4030",
        GrayLight = "#e8d8c0",
        GrayLighter = "#f5ead8",
    };

    public static readonly PaletteDark RebelAllianceDark = new()
    {
        Primary = "#ff8c2a",
        Secondary = "#d4a040",
        Tertiary = "#9a8870",
        Surface = "#2a1f15",
        Background = "#1c140c",
        BackgroundGray = "#15100a",
        AppbarText = "#e0c8a0",
        AppbarBackground = "rgba(28,20,12,0.9)",
        DrawerBackground = "#1c140c",
        TextPrimary = "#ffe9d0",
        TextSecondary = "#c8a878",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#c8a878",
        DrawerText = "#c8a878",
        GrayLight = "#3a2c1e",
        GrayLighter = "#251a10",
        Info = "#5a9bd4",
        Success = "#4ac06b",
        Warning = "#ffb545",
        Error = "#ff5050",
        LinesDefault = "#3a2c1e",
        TableLines = "#3a2c1e",
        Divider = "#322318",
    };

    // ── Galactic Empire — imperial red + gunmetal + obsidian ──

    public static readonly PaletteLight GalacticEmpireLight = new()
    {
        Primary = "#a82828",
        Secondary = "#2a2a2a",
        Tertiary = "#6e6e6e",
        AppbarText = "#1a1a1a",
        AppbarBackground = "rgba(245,245,245,0.95)",
        DrawerBackground = "#f5f5f5",
        Background = "#eeeeee",
        Surface = "#ffffff",
        TextPrimary = "#1a1a1a",
        TextSecondary = "#404040",
        GrayLight = "#d8d8d8",
        GrayLighter = "#eeeeee",
    };

    public static readonly PaletteDark GalacticEmpireDark = new()
    {
        Primary = "#cc1b1b",
        Secondary = "#7a7a7a",
        Tertiary = "#a82828",
        Surface = "#181818",
        Background = "#0a0a0a",
        BackgroundGray = "#050505",
        AppbarText = "#d0d0d0",
        AppbarBackground = "rgba(8,8,8,0.95)",
        DrawerBackground = "#0a0a0a",
        TextPrimary = "#dadada",
        TextSecondary = "#9a9a9a",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#9a9a9a",
        DrawerText = "#9a9a9a",
        GrayLight = "#2a2a2a",
        GrayLighter = "#181818",
        Info = "#4a86ff",
        Success = "#3dcb6c",
        Warning = "#ffb545",
        Error = "#ff3f5f",
        LinesDefault = "#2e2e2e",
        TableLines = "#2e2e2e",
        Divider = "#252525",
    };

    // ── Jedi Order — saber blue + parchment + bronze ──

    public static readonly PaletteLight JediOrderLight = new()
    {
        Primary = "#1f7ad6",
        Secondary = "#a8852f",
        Tertiary = "#5f8b5f",
        AppbarText = "#1a2840",
        AppbarBackground = "rgba(253,248,233,0.9)",
        DrawerBackground = "#fbf5e3",
        Background = "#f7f1de",
        Surface = "#fffaf0",
        TextPrimary = "#1a2840",
        TextSecondary = "#48586a",
        GrayLight = "#d8d0b8",
        GrayLighter = "#ece5cf",
    };

    public static readonly PaletteDark JediOrderDark = new()
    {
        Primary = "#5cb3ff",
        Secondary = "#d4b76a",
        Tertiary = "#7a9f7a",
        Surface = "#182838",
        Background = "#0e1a26",
        BackgroundGray = "#0a131c",
        AppbarText = "#c8d4e0",
        AppbarBackground = "rgba(14,26,38,0.9)",
        DrawerBackground = "#0e1a26",
        TextPrimary = "#dde6f0",
        TextSecondary = "#a0b4c8",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#a0b4c8",
        DrawerText = "#a0b4c8",
        GrayLight = "#28384a",
        GrayLighter = "#192836",
        Info = "#5cb3ff",
        Success = "#4ac06b",
        Warning = "#ffb545",
        Error = "#ff5a5a",
        LinesDefault = "#28384a",
        TableLines = "#28384a",
        Divider = "#1f2d3c",
    };

    // ── Sith Order — crimson saber + obsidian ──

    public static readonly PaletteLight SithOrderLight = new()
    {
        Primary = "#cc0000",
        Secondary = "#3a0a0a",
        Tertiary = "#8a0a0a",
        AppbarText = "#2a0808",
        AppbarBackground = "rgba(248,232,232,0.95)",
        DrawerBackground = "#f5e0e0",
        Background = "#f0d8d8",
        Surface = "#fff0f0",
        TextPrimary = "#2a0808",
        TextSecondary = "#5a1818",
        GrayLight = "#e0c0c0",
        GrayLighter = "#f0d8d8",
    };

    public static readonly PaletteDark SithOrderDark = new()
    {
        Primary = "#ff1a1a",
        Secondary = "#8a0a0a",
        Tertiary = "#cc4444",
        Surface = "#1a0808",
        Background = "#0a0202",
        BackgroundGray = "#050000",
        AppbarText = "#e8c8c8",
        AppbarBackground = "rgba(15,3,3,0.95)",
        DrawerBackground = "#0a0202",
        TextPrimary = "#f0d0d0",
        TextSecondary = "#c89898",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#c89898",
        DrawerText = "#c89898",
        GrayLight = "#2a1010",
        GrayLighter = "#1a0808",
        Info = "#4a86ff",
        Success = "#3dcb6c",
        Warning = "#ffb545",
        Error = "#ff5a5a",
        LinesDefault = "#3a1818",
        TableLines = "#3a1818",
        Divider = "#2a1010",
    };

    // ── Mandalorian Clans — beskar steel + clan blue + bronze sigil ──

    public static readonly PaletteLight MandalorianClansLight = new()
    {
        Primary = "#3a6b9c",
        Secondary = "#88898d",
        Tertiary = "#c4862a",
        AppbarText = "#1a2c40",
        AppbarBackground = "rgba(240,242,245,0.95)",
        DrawerBackground = "#eef0f3",
        Background = "#e8ebf0",
        Surface = "#ffffff",
        TextPrimary = "#1a2c40",
        TextSecondary = "#48586a",
        GrayLight = "#c8ccd4",
        GrayLighter = "#e0e4ea",
    };

    public static readonly PaletteDark MandalorianClansDark = new()
    {
        Primary = "#5a8bbe",
        Secondary = "#b0b0b8",
        Tertiary = "#d49a3e",
        Surface = "#1a242e",
        Background = "#0e151c",
        BackgroundGray = "#0a1015",
        AppbarText = "#c8d4e0",
        AppbarBackground = "rgba(14,21,28,0.9)",
        DrawerBackground = "#0e151c",
        TextPrimary = "#dde6f0",
        TextSecondary = "#9faab8",
        TextDisabled = "#ffffff33",
        DrawerIcon = "#9faab8",
        DrawerText = "#9faab8",
        GrayLight = "#283340",
        GrayLighter = "#19222c",
        Info = "#5cb3ff",
        Success = "#3dcb6c",
        Warning = "#ffb545",
        Error = "#ff5a5a",
        LinesDefault = "#283340",
        TableLines = "#283340",
        Divider = "#1c2530",
    };
}
