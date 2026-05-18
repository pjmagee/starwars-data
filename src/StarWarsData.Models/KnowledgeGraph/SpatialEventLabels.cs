namespace StarWarsData.Models.Entities;

/// <summary>
/// The single registered-once catalogue of "an event happened at this place"
/// (Design-032). Backend (<c>EventsAtLocationService</c>), the citation
/// resolver (Design-030), and any future Holocron / Phase-2 builders share
/// these two sets so the classification can't drift between query sites
/// (ADR-002 — type-and-label catalogues live in the model layer).
///
/// An edge counts as a spatial-event edge when BOTH hold:
/// <list type="bullet">
///   <item>the edge <c>label</c> is in <see cref="Labels"/>, and</item>
///   <item>the source node's <c>type</c> is in <see cref="EventFamily"/>.</item>
/// </list>
///
/// In <c>starwars-dev</c> (probed 2026-05-18) the only label events actually
/// use to reach a location is <c>took_place_at</c>; the rest of the list is
/// kept for forward-compatibility with ETL/Holocron passes that may emit the
/// other verbs. If a future builder introduces a new event-like type or a new
/// "happened here" verb, add it here — not in a query.
/// </summary>
public static class SpatialEventLabels
{
    /// <summary>Edge labels whose target is the place the event occurred.</summary>
    public static readonly IReadOnlySet<string> Labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "took_place_at",
        "happened_at",
        "happened_in",
        "hosted_battle",
        "fought_at",
        "located_at",
        "occurred_at",
        "site_of",
        "conducted_at",
    };

    /// <summary>
    /// Source node <see cref="GraphNode.Type"/> values that represent an
    /// in-universe event. <c>Duel</c> is included on top of the Design-032
    /// list because the corpus uses <c>Duel</c> nodes with <c>took_place_at</c>
    /// edges (e.g. "Duel on Yavin 4").
    /// </summary>
    public static readonly IReadOnlySet<string> EventFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        KgNodeTypes.Battle,
        KgNodeTypes.Mission,
        KgNodeTypes.Campaign,
        KgNodeTypes.War,
        KgNodeTypes.Event,
        KgNodeTypes.Duel,
        "Siege",
        "Conflict",
        "Attack",
        "Operation",
        "Skirmish",
        "Encounter",
    };

    /// <summary>
    /// Coarse display bucket for an event node type, used by the galaxy-map
    /// "Events at this location" filter chips. Anything not specially mapped
    /// falls back to "Event".
    /// </summary>
    public static string CategoryFor(string? nodeType) =>
        nodeType switch
        {
            KgNodeTypes.Battle => "Battle",
            KgNodeTypes.War => "War",
            KgNodeTypes.Campaign => "Campaign",
            KgNodeTypes.Mission => "Mission",
            KgNodeTypes.Duel => "Duel",
            "Siege" => "Siege",
            "Attack" => "Attack",
            "Operation" => "Operation",
            "Skirmish" => "Skirmish",
            "Encounter" => "Encounter",
            "Conflict" => "Conflict",
            _ => "Event",
        };
}
