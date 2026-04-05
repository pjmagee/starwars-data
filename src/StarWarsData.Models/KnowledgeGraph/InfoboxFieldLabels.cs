namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical Wookieepedia infobox field-label strings (the <c>Label</c> values
/// stored inside <see cref="Infobox.Data"/>). These are the display names
/// used by Wookieepedia portable-infobox templates — they are NOT KG edge
/// labels (see <see cref="KgLabels"/>) nor KG node types (see <see cref="KgNodeTypes"/>).
/// </summary>
/// <remarks>
/// Only the labels referenced from C# code are included. Labels that only
/// appear as keys in <c>FieldSemantics</c> live in that source-of-truth
/// dictionary and do not need constants here.
/// </remarks>
public static class InfoboxFieldLabels
{
    // ── Geography / map coordinates ──
    public const string GridSquare = "Grid square";
    public const string Region = "Region";
    public const string RegionPlural = "Region(s)";
    public const string Sector = "Sector";
    public const string System = "System";
    public const string CelestialBody = "Celestial body";

    // ── Location-like labels used for BFS location resolution ──
    public const string Place = "Place";
    public const string Location = "Location";
    public const string Headquarters = "Headquarters";
    public const string Capital = "Capital";

    // ── Classification ──
    public const string Class = "Class";

    // ── Identity ──
    public const string Titles = "Titles";

    // ── Battle / event result ──
    public const string Outcome = "Outcome";

    // ── Generic temporal marker ──
    public const string Date = "Date";

    /// <summary>
    /// Labels searched during BFS location-resolution: any field with one of
    /// these names on a page is treated as a candidate location reference.
    /// Ordered by priority — earlier entries win if a page has multiple.
    /// </summary>
    public static readonly IReadOnlyList<string> LocationLike = [Place, Location, System, Headquarters, Capital, GridSquare, CelestialBody];
}
