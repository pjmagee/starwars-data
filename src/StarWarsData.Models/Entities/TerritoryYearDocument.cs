using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A complete pre-computed territory year document. One document per event year,
/// containing everything the frontend needs — no dynamic queries at request time.
/// Built by TerritoryInferenceService ETL from kg.nodes + kg.edges.
/// </summary>
public class TerritoryYearDocument
{
    [BsonId]
    public int Year { get; set; }

    [BsonElement("yearDisplay")]
    public string YearDisplay { get; set; } = string.Empty;

    [BsonElement("era")]
    public string? Era { get; set; }

    [BsonElement("eraDescription")]
    public string? EraDescription { get; set; }

    [BsonElement("regions")]
    public List<TerritoryRegionControl> Regions { get; set; } = [];

    [BsonElement("keyEvents")]
    public List<TerritoryKeyEvent> KeyEvents { get; set; } = [];
}

/// <summary>
/// Pre-computed overview document — stored as a single document,
/// loaded once on page init.
/// </summary>
public class TerritoryOverviewDocument
{
    [BsonId]
    public string Id { get; set; } = "overview";

    [BsonElement("minYear")]
    public int MinYear { get; set; }

    [BsonElement("maxYear")]
    public int MaxYear { get; set; }

    [BsonElement("availableYears")]
    public List<int> AvailableYears { get; set; } = [];

    [BsonElement("factions")]
    public List<TerritoryFactionInfo> Factions { get; set; } = [];

    [BsonElement("eras")]
    public List<TerritoryEra> Eras { get; set; } = [];

    [BsonElement("regions")]
    public List<string> Regions { get; set; } = [];
}

/// <summary>
/// Pre-computed faction metadata — baked into the overview document.
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
