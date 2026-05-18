namespace StarWarsData.Models.Queries;

/// <summary>
/// Payload for <c>GET /api/galaxy-map/locations/{pageId}/events</c> — every
/// event the KG records as having happened at the focused spatial entity
/// (Design-032). Date-agnostic: not scoped to a single year, unlike Timeline
/// mode. Sorted by <see cref="LocationEvent.Year"/> ascending (BBY → ABY),
/// nullable years last.
/// </summary>
public sealed class EventsAtLocationResult
{
    public required int LocationId { get; init; }
    public required string LocationName { get; init; }
    public required int TotalEvents { get; init; }
    public required IReadOnlyList<LocationEvent> Events { get; init; }

    /// <summary>
    /// What the list covers (Design-032 Phase 2 roll-up). <c>"location"</c> =
    /// direct edges only (a planet / trade route). <c>"system"</c> /
    /// <c>"sector"</c> / <c>"region"</c> = the focal node plus every event at
    /// the descendants it contains (its worlds, its systems, its sectors), so
    /// a user standing on a system sees the battles fought on its planets.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>Number of child locations folded into a roll-up; 0 for a direct list.</summary>
    public int RolledUpLocations { get; init; }
}

/// <summary>One event row in <see cref="EventsAtLocationResult"/>.</summary>
public sealed class LocationEvent
{
    public required int PageId { get; init; }
    public required string Name { get; init; }

    /// <summary>The KG node type, e.g. <c>Battle</c>, <c>Mission</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Coarse display bucket (Battle / Mission / War / …).</summary>
    public required string Category { get; init; }

    public required string Continuity { get; init; }

    /// <summary>Sort-key year (negative = BBY, positive = ABY). Null when unknown.</summary>
    public int? Year { get; init; }

    public required string YearDisplay { get; init; }

    public string? WikiUrl { get; init; }

    /// <summary>The most-specific spatial edge label that placed this event here.</summary>
    public required string EdgeLabel { get; init; }
}
