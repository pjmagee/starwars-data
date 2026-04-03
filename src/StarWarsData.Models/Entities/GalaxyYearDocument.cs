using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Pre-computed unified galaxy year document. One per event year in the galaxy.years collection.
/// Contains territory control AND event heatmap data — no dynamic queries at request time.
/// Built by GalaxyMapETLService from kg.nodes + kg.edges + timeline.* collections.
/// </summary>
public class GalaxyYearDocument
{
    [BsonId]
    public int Year { get; set; }

    [BsonElement("yearDisplay")]
    public string YearDisplay { get; set; } = string.Empty;

    [BsonElement("era")]
    public string? Era { get; set; }

    [BsonElement("eraDescription")]
    public string? EraDescription { get; set; }

    // ── Territory layer ──

    [BsonElement("regions")]
    public List<TerritoryRegionControl> Regions { get; set; } = [];

    // ── Events layer ──

    [BsonElement("eventCells")]
    public List<GalaxyYearEventCell> EventCells { get; set; } = [];

    /// <summary>Events that could not be resolved to a grid location.</summary>
    [BsonElement("unresolvedEvents")]
    public List<GalaxyYearEvent> UnresolvedEvents { get; set; } = [];

    [BsonElement("lensCounts")]
    public Dictionary<string, int> LensCounts { get; set; } = new();
}

/// <summary>
/// Pre-computed overview for the unified galaxy map. Stored as _id="overview" in galaxy.years.
/// </summary>
public class GalaxyOverviewDocument
{
    [BsonId]
    public string Id { get; set; } = "overview";

    [BsonElement("minYear")]
    public int MinYear { get; set; }

    [BsonElement("maxYear")]
    public int MaxYear { get; set; }

    // ── Grid bounds (computed from actual data) ──

    [BsonElement("gridColumns")]
    public int GridColumns { get; set; } = 26;

    [BsonElement("gridRows")]
    public int GridRows { get; set; } = 21;

    [BsonElement("gridStartCol")]
    public int GridStartCol { get; set; }

    [BsonElement("gridStartRow")]
    public int GridStartRow { get; set; }

    [BsonElement("availableYears")]
    public List<int> AvailableYears { get; set; } = [];

    // ── Territory metadata ──

    [BsonElement("factions")]
    public List<TerritoryFactionInfo> Factions { get; set; } = [];

    [BsonElement("galacticRegions")]
    public List<string> GalacticRegions { get; set; } = [];

    [BsonElement("eras")]
    public List<TerritoryEra> Eras { get; set; } = [];

    // ── Trade routes (pre-computed from KG edges) ──

    [BsonElement("tradeRoutes")]
    public List<GalaxyTradeRoute> TradeRoutes { get; set; } = [];

    // ── Event metadata ──

    [BsonElement("lenses")]
    public List<GalaxyLensSummary> Lenses { get; set; } = [];

    [BsonElement("yearDensity")]
    public List<GalaxyYearDensity> YearDensity { get; set; } = [];
}

/// <summary>
/// A grid cell containing aggregated events for a single year.
/// </summary>
public class GalaxyYearEventCell
{
    [BsonElement("col")]
    public int Col { get; set; }

    [BsonElement("row")]
    public int Row { get; set; }

    [BsonElement("region")]
    public string? Region { get; set; }

    [BsonElement("count")]
    public int Count { get; set; }

    [BsonElement("events")]
    public List<GalaxyYearEvent> Events { get; set; } = [];
}

/// <summary>
/// A single event placed on the galaxy map grid, pre-resolved to a cell location.
/// </summary>
public class GalaxyYearEvent
{
    [BsonElement("pageId")]
    public int? PageId { get; set; }

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("lens")]
    public string Lens { get; set; } = string.Empty;

    [BsonElement("category")]
    public string? Category { get; set; }

    [BsonElement("place")]
    public string? Place { get; set; }

    [BsonElement("outcome")]
    public string? Outcome { get; set; }

    [BsonElement("wikiUrl")]
    public string? WikiUrl { get; set; }

    [BsonElement("imageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("universe")]
    [BsonRepresentation(BsonType.String)]
    public Universe Universe { get; set; } = Universe.Unknown;
}

/// <summary>
/// Per-lens summary (total events + events with grid locations).
/// </summary>
public class GalaxyLensSummary
{
    [BsonElement("lens")]
    public string Lens { get; set; } = string.Empty;

    [BsonElement("totalCount")]
    public int TotalCount { get; set; }

    [BsonElement("withLocationCount")]
    public int WithLocationCount { get; set; }

    /// <summary>
    /// Whether this lens is primarily out-of-universe content (books, comics, TV episodes, etc.)
    /// Used by the frontend to hide these when the global "Out of Universe" filter is off.
    /// </summary>
    [BsonElement("outOfUniverse")]
    public bool OutOfUniverse { get; set; }
}

/// <summary>
/// Per-year event density with per-lens breakdown for timeline scrubber.
/// </summary>
public class GalaxyYearDensity
{
    [BsonElement("year")]
    public int Year { get; set; }

    [BsonElement("count")]
    public int Count { get; set; }

    [BsonElement("canonCount")]
    public int CanonCount { get; set; }

    [BsonElement("legendsCount")]
    public int LegendsCount { get; set; }

    [BsonElement("perLens")]
    public Dictionary<string, int> PerLens { get; set; } = new();
}

/// <summary>
/// A trade route pre-computed from KG edges, with waypoints resolved to grid coordinates.
/// </summary>
public class GalaxyTradeRoute
{
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("pageId")]
    public int PageId { get; set; }

    [BsonElement("regions")]
    public List<string> Regions { get; set; } = [];

    [BsonElement("endpoints")]
    public List<GalaxyTradeRouteWaypoint> Endpoints { get; set; } = [];

    [BsonElement("waypoints")]
    public List<GalaxyTradeRouteWaypoint> Waypoints { get; set; } = [];

    [BsonElement("junctions")]
    public List<string> Junctions { get; set; } = [];
}

/// <summary>
/// A waypoint on a trade route, resolved to grid coordinates via the KG.
/// </summary>
public class GalaxyTradeRouteWaypoint
{
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("pageId")]
    public int PageId { get; set; }

    [BsonElement("col")]
    public int? Col { get; set; }

    [BsonElement("row")]
    public int? Row { get; set; }

    [BsonElement("region")]
    public string? Region { get; set; }
}
