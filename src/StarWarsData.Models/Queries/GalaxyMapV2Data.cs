namespace StarWarsData.Models.Queries;

/// <summary>
/// Lightweight overview payload: regions, trade routes, nebulas. No systems.
/// Loaded once on page init (~300 elements).
/// </summary>
public class GalaxyMapV2Overview
{
    public int GridColumns { get; set; } = 26;
    public int GridRows { get; set; } = 20;
    public List<MapV2Region> Regions { get; set; } = [];
    public List<MapV2TradeRoute> TradeRoutes { get; set; } = [];
    public List<MapV2Nebula> Nebulas { get; set; } = [];
    /// <summary>
    /// Per-cell system counts for the overview heatmap. Only populated cells included.
    /// </summary>
    public List<MapV2CellSummary> Cells { get; set; } = [];
}

public class MapV2CellSummary
{
    public int Col { get; set; }
    public int Row { get; set; }
    public int SystemCount { get; set; }
    public string? Region { get; set; }
}

/// <summary>
/// Systems within a requested grid range. Loaded on-demand as user zooms in.
/// </summary>
public class GalaxyMapV2Systems
{
    public int MinCol { get; set; }
    public int MaxCol { get; set; }
    public int MinRow { get; set; }
    public int MaxRow { get; set; }
    public List<MapV2System> Systems { get; set; } = [];
}

public class MapV2System
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Col { get; set; }   // 0-25 (A=0, Z=25)
    public int Row { get; set; }   // 0-19 (grid 1=row 0, grid 20=row 19)
    public string? Region { get; set; }
    public string? Sector { get; set; }
    public List<MapV2CelestialBody> CelestialBodies { get; set; } = [];
}

public class MapV2CelestialBody
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Class { get; set; }
}

public class MapV2Region
{
    public string Name { get; set; } = "";
    /// <summary>
    /// Grid cells belonging to this region, as [col, row] pairs (0-indexed)
    /// </summary>
    public List<int[]> Cells { get; set; } = [];
}

public class MapV2TradeRoute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MapV2Waypoint> Waypoints { get; set; } = [];
}

public class MapV2Waypoint
{
    public string Name { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
}

public class MapV2Nebula
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
