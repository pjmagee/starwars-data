namespace StarWarsData.Services;

/// <summary>
/// Canonical ordered list of galactic regions used by the galaxy-map ETL passes.
/// Matches the ordering displayed on Wookieepedia galaxy maps (core out to rim).
/// </summary>
public static class GalacticRegions
{
    public const string DeepCore = "Deep Core";
    public const string CoreWorlds = "Core Worlds";
    public const string Colonies = "Colonies";
    public const string InnerRim = "Inner Rim";
    public const string ExpansionRegion = "Expansion Region";
    public const string MidRim = "Mid Rim";
    public const string OuterRimTerritories = "Outer Rim Territories";
    public const string HuttSpace = "Hutt Space";
    public const string UnknownRegions = "Unknown Regions";
    public const string WildSpace = "Wild Space";

    /// <summary>All regions in canonical core-out ordering.</summary>
    public static readonly IReadOnlyList<string> All = [DeepCore, CoreWorlds, Colonies, InnerRim, ExpansionRegion, MidRim, OuterRimTerritories, HuttSpace, UnknownRegions, WildSpace];

    /// <summary>
    /// Normalise a free-text region name from raw infobox data to one of the
    /// canonical <see cref="All"/> values. Returns null if the input doesn't
    /// resemble any known region.
    /// </summary>
    public static string? Normalise(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim();
        foreach (var region in All)
        {
            if (lower == region.ToLowerInvariant())
                return region;
        }
        if (lower.Contains("outer rim"))
            return OuterRimTerritories;
        if (lower.Contains("mid rim"))
            return MidRim;
        if (lower.Contains("inner rim"))
            return InnerRim;
        if (lower.Contains("core world"))
            return CoreWorlds;
        if (lower.Contains("deep core"))
            return DeepCore;
        if (lower.Contains("colonies"))
            return Colonies;
        if (lower.Contains("expansion"))
            return ExpansionRegion;
        if (lower.Contains("hutt space"))
            return HuttSpace;
        if (lower.Contains("unknown region"))
            return UnknownRegions;
        if (lower.Contains("wild space"))
            return WildSpace;
        return null;
    }
}
