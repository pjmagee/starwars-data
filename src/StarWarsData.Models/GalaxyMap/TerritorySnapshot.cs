using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One galactic region's faction control mix for a single year.
/// Embedded in <see cref="GalaxyYearDocument.Regions"/> by <c>GalaxyMapETLService</c>.
/// </summary>
public class TerritoryRegionControl
{
    public string Region { get; set; } = string.Empty;
    public List<TerritoryFactionControl> Factions { get; set; } = [];
}

/// <summary>
/// A single faction's control share within a region for a year.
/// </summary>
public class TerritoryFactionControl
{
    public string Faction { get; set; } = string.Empty;
    public double Control { get; set; }
    public bool Contested { get; set; }
    public string Color { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>
/// Era band metadata embedded in <see cref="GalaxyOverviewDocument.Eras"/>.
/// </summary>
public class TerritoryEra
{
    public string Name { get; set; } = string.Empty;
    public int StartYear { get; set; }
    public int EndYear { get; set; }
    public string? Description { get; set; }
    public List<string> Conflicts { get; set; } = [];
    public List<string> ImportantEvents { get; set; } = [];

    /// <summary>
    /// Continuity of the source Era node. Stored as a string ("Canon"/"Legends"/
    /// "Both"/"Unknown") so it can be filtered at the DB level with a
    /// <c>$filter</c> aggregation stage on the embedded Eras array.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;
}

/// <summary>
/// Key-event DTO retained for territory/timeline overlays (year-scoped narrative markers).
/// </summary>
public class TerritoryKeyEvent
{
    public int Year { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? WikiUrl { get; set; }
    public string? Category { get; set; }
    public string? Place { get; set; }
    public string? Region { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
}

/// <summary>
/// Pre-computed faction metadata — baked into the galaxy overview document
/// (<see cref="GalaxyOverviewDocument.Factions"/>) by <c>GalaxyMapETLService</c>.
/// </summary>
public class TerritoryFactionInfo
{
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("color")]
    public string Color { get; set; } = string.Empty;

    [BsonElement("wikiUrl")]
    public string? WikiUrl { get; set; }

    [BsonElement("iconUrl")]
    public string? IconUrl { get; set; }
}
