namespace StarWarsData.Models.Queries;

/// <summary>
/// Resolves a KG node id to whatever the galaxy-map JS module needs to drill
/// to it. Returned by GET /api/galaxy-map/locate/{pageId}; consumed by the
/// <c>/galaxy-map/{PageId:int}</c> deep-link route handler in the Frontend.
///
/// Kind is the KG node Type — one of <c>System</c>, <c>CelestialBody</c>,
/// <c>Sector</c>, <c>Region</c>, <c>TradeRoute</c>, or <c>Other</c> when the
/// pageId exists but isn't placed on the map (a Character, Battle, etc.).
/// SystemId is set only for <c>CelestialBody</c> — the system the body lives
/// in, found via the <c>in_system</c> edge.
/// </summary>
public sealed class MapLocateResult
{
    public required int PageId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Continuity { get; init; }
    public int? SystemId { get; init; }
    public string? Region { get; init; }
    public string? Sector { get; init; }

    /// <summary>
    /// Grid column (0-25, A-Z) of the entity's location. For Systems this is
    /// the system's own grid square; for CelestialBodies it's the parent
    /// system's grid square. Null for Sector/Region/TradeRoute and for
    /// anything not pinned to the grid.
    /// </summary>
    public int? Col { get; init; }

    /// <summary>Grid row (0-19, 1-20). See <see cref="Col"/>.</summary>
    public int? Row { get; init; }
}
