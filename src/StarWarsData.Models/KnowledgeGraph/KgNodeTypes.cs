namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical entity type strings stored in <see cref="GraphNode.Type"/>.
/// These mirror the Wookieepedia infobox template names one-to-one.
/// Use these constants instead of scattered string literals so a rename in
/// the ETL is a single-point change.
/// </summary>
public static class KgNodeTypes
{
    // ── People ──
    public const string Character = "Character";
    public const string Person = "Person";
    public const string Family = "Family";
    public const string Species = "Species";

    // ── Geography / Places ──
    public const string CelestialBody = "CelestialBody";
    public const string Location = "Location";
    public const string City = "City";
    public const string Structure = "Structure";
    public const string System = "System";
    public const string Sector = "Sector";
    public const string Region = "Region";
    public const string Nebula = "Nebula";

    // ── Politics / Military ──
    public const string Government = "Government";
    public const string Organization = "Organization";
    public const string Military = "Military";

    // ── Events / Conflict ──
    public const string Battle = "Battle";
    public const string War = "War";
    public const string Campaign = "Campaign";
    public const string Mission = "Mission";
    public const string Duel = "Duel";
    public const string Election = "Election";
    public const string Event = "Event";
    public const string Treaty = "Treaty";
    public const string Era = "Era";
    public const string Year = "Year";

    // ── Vehicles / Ships ──
    public const string Starship = "Starship";
    public const string StarshipClass = "StarshipClass";
    public const string SpaceStation = "SpaceStation";
    public const string Vehicle = "Vehicle";
    public const string AirVehicle = "AirVehicle";
    public const string GroundVehicle = "GroundVehicle";
    public const string TradeRoute = "TradeRoute";

    // ── Things ──
    public const string Weapon = "Weapon";
    public const string Lightsaber = "Lightsaber";
    public const string Device = "Device";
    public const string Artifact = "Artifact";
    public const string Droid = "Droid";

    // ── Qualifier / trait nodes (dropped as relationship targets — they are attributes, not entities) ──
    public const string TitleOrPosition = "TitleOrPosition";
    public const string ForcePower = "ForcePower";
    public const string LightsaberForm = "LightsaberForm";

    /// <summary>Fallback type used when an infobox template cannot be resolved.</summary>
    public const string Unknown = "Unknown";

    // ── Media (real-world) ──
    public const string Book = "Book";
    public const string Movie = "Movie";
    public const string Comic = "Comic";
    public const string Game = "Game";

    /// <summary>
    /// Node types that represent in-universe events for the galaxy map timeline.
    /// Used by ETL passes that heatmap-rank entities along a timeline.
    /// </summary>
    public static readonly IReadOnlySet<string> EventLike = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Battle, War, Campaign, Government, Treaty, Event };
}
