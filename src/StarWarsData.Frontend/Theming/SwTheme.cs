namespace StarWarsData.Frontend.Theming;

/// <summary>
/// Star Wars faction-themed colour schemes available via the theme picker.
/// Each value maps to a <see cref="MudBlazor.MudTheme"/> with both light and dark
/// palettes via <see cref="Themes.GetTheme"/>.
/// </summary>
public enum SwTheme
{
    Holonet = 0,
    RebelAlliance = 1,
    GalacticEmpire = 2,
    JediOrder = 3,
    SithOrder = 4,
    MandalorianClans = 5,
}

public static class SwThemeExtensions
{
    public static string Id(this SwTheme theme) =>
        theme switch
        {
            SwTheme.Holonet => "holonet",
            SwTheme.RebelAlliance => "rebel",
            SwTheme.GalacticEmpire => "empire",
            SwTheme.JediOrder => "jedi",
            SwTheme.SithOrder => "sith",
            SwTheme.MandalorianClans => "mandalorian",
            _ => "holonet",
        };

    public static string Label(this SwTheme theme) =>
        theme switch
        {
            SwTheme.Holonet => "Holonet",
            SwTheme.RebelAlliance => "Rebel Alliance",
            SwTheme.GalacticEmpire => "Galactic Empire",
            SwTheme.JediOrder => "Jedi Order",
            SwTheme.SithOrder => "Sith Order",
            SwTheme.MandalorianClans => "Mandalorian Clans",
            _ => "Holonet",
        };

    public static SwTheme FromId(string? id) =>
        id switch
        {
            "rebel" => SwTheme.RebelAlliance,
            "empire" => SwTheme.GalacticEmpire,
            "jedi" => SwTheme.JediOrder,
            "sith" => SwTheme.SithOrder,
            "mandalorian" => SwTheme.MandalorianClans,
            _ => SwTheme.Holonet,
        };
}
