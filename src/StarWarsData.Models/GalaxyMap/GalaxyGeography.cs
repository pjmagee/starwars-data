namespace StarWarsData.Models.Queries;

/// <summary>
/// Lightweight overview payload: regions, trade routes, nebulas. No systems.
/// Loaded once on page init (~300 elements).
/// </summary>
public class GalaxyGeography
{
    public int GridColumns { get; set; } = 26;
    public int GridRows { get; set; } = 21;
    public int GridStartCol { get; set; }
    public int GridStartRow { get; set; }
    public List<GeoRegion> Regions { get; set; } = [];
    public List<GeoTradeRoute> TradeRoutes { get; set; } = [];
    public List<GeoNebula> Nebulas { get; set; } = [];

    /// <summary>
    /// Per-cell system counts for the overview heatmap. Only populated cells included.
    /// </summary>
    public List<GeoCellSummary> Cells { get; set; } = [];
}

public class GeoCellSummary
{
    public int Col { get; set; }
    public int Row { get; set; }
    public int SystemCount { get; set; }
    public string? Region { get; set; }
}

/// <summary>
/// Systems within a requested grid range. Loaded on-demand as user zooms in.
/// </summary>
public class GalaxyGeographySystems
{
    public int MinCol { get; set; }
    public int MaxCol { get; set; }
    public int MinRow { get; set; }
    public int MaxRow { get; set; }
    public List<GeoSystem> Systems { get; set; } = [];
}

public class GeoSystem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Col { get; set; } // 0-25 (A=0, Z=25)
    public int Row { get; set; } // 0-19 (grid 1=row 0, grid 20=row 19)
    public string? Region { get; set; }
    public string? Sector { get; set; }
    public List<GeoCelestialBody> CelestialBodies { get; set; } = [];
}

public class GeoCelestialBody
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Class { get; set; }
}

public class GeoRegion
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Grid cells belonging to this region, as [col, row] pairs (0-indexed)
    /// </summary>
    public List<int[]> Cells { get; set; } = [];
}

public class GeoTradeRoute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<GeoWaypoint> Waypoints { get; set; } = [];
}

public class GeoWaypoint
{
    public string Name { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
}

public class GeoNebula
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }

    /// <summary>
    /// All grid cells this nebula spans. First entry matches Col/Row.
    /// </summary>
    public List<int[]> Cells { get; set; } = [];
    public string? Region { get; set; }
}
