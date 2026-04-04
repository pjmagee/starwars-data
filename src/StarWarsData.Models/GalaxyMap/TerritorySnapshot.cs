using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A snapshot of galactic territory control at a specific year.
/// Each document represents one faction's control over one galactic region in one year.
/// </summary>
public class TerritorySnapshot
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Year in sort-key format (negative = BBY, positive = ABY).</summary>
    [BsonElement("year")]
    public int Year { get; set; }

    /// <summary>Galactic region name (e.g. "Core Worlds", "Outer Rim Territories").</summary>
    [BsonElement("region")]
    public string Region { get; set; } = string.Empty;

    /// <summary>Controlling faction name (e.g. "Galactic Empire", "New Republic").</summary>
    [BsonElement("faction")]
    public string Faction { get; set; } = string.Empty;

    /// <summary>Control strength 0.0–1.0 (1.0 = full control, 0.5 = contested).</summary>
    [BsonElement("control")]
    public double Control { get; set; } = 1.0;

    /// <summary>Whether this region is actively contested between factions.</summary>
    [BsonElement("contested")]
    public bool Contested { get; set; }

    /// <summary>Hex color for the faction (e.g. "#ff0000").</summary>
    [BsonElement("color")]
    public string Color { get; set; } = string.Empty;

    /// <summary>Brief note about what happened (e.g. "Post-Battle of Endor fragmentation").</summary>
    [BsonElement("note")]
    public string? Note { get; set; }
}

/// <summary>
/// API response: all faction controls for a single year.
/// </summary>
public class TerritoryYearResponse
{
    public int Year { get; set; }
    public string YearDisplay { get; set; } = string.Empty;
    public string? Era { get; set; }
    public string? EraDescription { get; set; }
    public List<string> EraConflicts { get; set; } = [];
    public List<string> EraImportantEvents { get; set; } = [];
    public List<TerritoryRegionControl> Regions { get; set; } = [];
    public List<TerritoryKeyEvent> KeyEvents { get; set; } = [];
}

public class TerritoryRegionControl
{
    public string Region { get; set; } = string.Empty;
    public List<TerritoryFactionControl> Factions { get; set; } = [];
}

public class TerritoryFactionControl
{
    public string Faction { get; set; } = string.Empty;
    public double Control { get; set; }
    public bool Contested { get; set; }
    public string Color { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>
/// API response: overview of available territory data.
/// </summary>
public class TerritoryOverview
{
    public int MinYear { get; set; }
    public int MaxYear { get; set; }
    public List<string> Factions { get; set; } = [];
    public List<string> Regions { get; set; } = [];
    public List<TerritoryEra> Eras { get; set; } = [];

    /// <summary>Sorted list of years that have territory snapshot data (sparse).</summary>
    public List<int> AvailableYears { get; set; } = [];
}

public class TerritoryEra
{
    public string Name { get; set; } = string.Empty;
    public int StartYear { get; set; }
    public int EndYear { get; set; }
    public string? Description { get; set; }
    public List<string> Conflicts { get; set; } = [];
    public List<string> ImportantEvents { get; set; } = [];
}

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
