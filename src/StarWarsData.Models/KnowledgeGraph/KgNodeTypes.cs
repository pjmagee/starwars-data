namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical entity type strings stored in <see cref="GraphNode.Type"/>.
/// These mirror the Wookieepedia infobox template names one-to-one.
/// Use these constants instead of scattered string literals so a rename in
/// the ETL is a single-point change.
///
/// Coverage rule: every node type with ≥100 nodes in <c>kg.nodes</c> has a
/// constant here. The unit test <c>NodeBuilderCoverageTests</c> asserts this.
/// Tail-truncated types (≤100 nodes) may use raw string literals at the call
/// site since they're rare and unlikely to drive per-type semantics.
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
    public const string Star = "Star";
    public const string Plant = "Plant";

    // ── Politics / Military ──
    public const string Government = "Government";
    public const string Organization = "Organization";
    public const string Company = "Company";
    public const string RealCompany = "RealCompany";

    /// <summary>
    /// Military unit (battalion, legion, fleet sub-unit). NOTE: the type-name
    /// string is "Military_unit" with underscore, matching Wookieepedia's
    /// template suffix. Earlier KgNodeTypes had a dead <c>Military</c> constant
    /// that didn't match the corpus and made <c>MilitaryNodeBuilder</c> a no-op
    /// since Design-007. Renamed during the typed-NodeBuilders cleanup pass.
    /// </summary>
    public const string MilitaryUnit = "Military_unit";
    public const string Fleet = "Fleet";
    public const string Religion = "Religion";
    public const string Deity = "Deity";

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
    public const string Tactics = "Tactics";
    public const string Disease = "Disease";

    // ── Vehicles / Ships ──
    public const string Starship = "Starship";
    public const string StarshipClass = "StarshipClass";
    public const string IndividualShip = "IndividualShip";
    public const string SpaceStation = "SpaceStation";
    public const string Vehicle = "Vehicle";
    public const string AirVehicle = "AirVehicle";
    public const string GroundVehicle = "GroundVehicle";
    public const string AquaticVehicle = "AquaticVehicle";
    public const string RepulsorliftVehicle = "RepulsorliftVehicle";
    public const string TradeRoute = "TradeRoute";

    // ── Things ──
    public const string Weapon = "Weapon";
    public const string Lightsaber = "Lightsaber";
    public const string Device = "Device";
    public const string Artifact = "Artifact";
    public const string Droid = "Droid";
    public const string DroidSeries = "DroidSeries";
    public const string Substance = "Substance";
    public const string Food = "Food";
    public const string Clothing = "Clothing";
    public const string Armor = "Armor";
    public const string Medal = "Medal";

    // ── Cultural / Language ──
    public const string Language = "Language";

    // ── Qualifier / trait nodes (dropped as relationship targets — they are attributes, not entities) ──
    public const string TitleOrPosition = "TitleOrPosition";
    public const string ForcePower = "ForcePower";
    public const string LightsaberForm = "LightsaberForm";

    /// <summary>Fallback type used when an infobox template cannot be resolved.</summary>
    public const string Unknown = "Unknown";

    // ── Media: real-world / publication ──
    public const string Book = "Book";
    public const string BookSeries = "BookSeries";
    public const string ReferenceBook = "ReferenceBook";
    public const string ShortStory = "ShortStory";
    public const string Audiobook = "Audiobook";
    public const string Movie = "Movie";
    public const string Comic = "Comic";
    public const string ComicBook = "ComicBook";
    public const string ComicStory = "ComicStory";
    public const string ComicMagazine = "ComicMagazine";
    public const string ComicCollection = "ComicCollection";
    public const string ComicSeries = "ComicSeries";
    public const string ComicArc = "ComicArc";
    public const string MagazineIssue = "MagazineIssue";
    public const string MagazineArticle = "MagazineArticle";
    public const string MagazineDepartment = "MagazineDepartment";
    public const string ReferenceMagazine = "ReferenceMagazine";
    public const string TelevisionEpisode = "TelevisionEpisode";
    public const string Game = "Game";
    public const string VideoGame = "VideoGame";
    public const string Adventure = "Adventure";
    public const string ActivityBook = "ActivityBook";
    public const string ExpansionPack = "ExpansionPack";
    public const string TradingCardSet = "TradingCardSet";
    public const string Music = "Music";
    public const string Soundtrack = "Soundtrack";
    public const string Band = "Band";
    public const string WebArticle = "WebArticle";
    public const string IuMedia = "IU_media";

    // ── Real-world ──
    public const string Store = "Store";

    /// <summary>
    /// Node types that represent in-universe events for the galaxy map timeline.
    /// Used by ETL passes that heatmap-rank entities along a timeline.
    /// </summary>
    public static readonly IReadOnlySet<string> EventLike = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Battle, War, Campaign, Government, Treaty, Event };
}
