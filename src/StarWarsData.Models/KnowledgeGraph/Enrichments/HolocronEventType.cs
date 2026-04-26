namespace StarWarsData.Models.Entities;

/// <summary>
/// Discrete state-transition event types written to <c>kg.events</c> by the Holocron agent.
/// The events collection is append-only and immutable — superseded enrichments don't
/// disappear from it, only their status flips in <c>kg.enrichments</c>.
/// </summary>
public enum HolocronEventType
{
    /// <summary>Agent created a new <see cref="NodeEnrichment"/>.</summary>
    EnrichmentCreated,

    /// <summary>Agent created a new <see cref="EdgeEnrichment"/>.</summary>
    EdgeEnrichmentCreated,

    /// <summary>An older enrichment was superseded by a newer one for the same target.</summary>
    EnrichmentSuperseded,

    /// <summary>Source node's <c>contentHash</c> changed; enrichment flagged for re-evaluation.</summary>
    EnrichmentMarkedStale,

    /// <summary>Agent or human flagged the enrichment as wrong.</summary>
    EnrichmentRejected,

    /// <summary>Source <see cref="GraphNode"/> no longer exists in <c>kg.nodes</c> (Wookieepedia deleted the page).</summary>
    EnrichmentOrphaned,

    /// <summary>Daily Holocron pass started — emits a single bookkeeping event per run.</summary>
    HolocronPassStarted,

    /// <summary>Daily Holocron pass completed — carries summary counts.</summary>
    HolocronPassCompleted,
}
