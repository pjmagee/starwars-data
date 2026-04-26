namespace StarWarsData.Models.Entities;

/// <summary>
/// Lifecycle state of a single agent-produced enrichment. Only <see cref="Active"/>
/// enrichments are surfaced through the <c>kg.nodes.enriched</c> / <c>kg.edges.enriched</c>
/// read views. Other states are retained in the base collection for the audit trail
/// (see <c>kg.events</c>) but hidden from consumers.
/// </summary>
public enum EnrichmentStatus
{
    /// <summary>Visible in the merged view. Latest active enrichment for a target wins.</summary>
    Active,

    /// <summary>Replaced by a newer enrichment for the same target. Hidden from the view.</summary>
    Superseded,

    /// <summary>Source node's <c>contentHash</c> changed since the enrichment was made; awaits Holocron re-evaluation.</summary>
    Stale,

    /// <summary>Agent or human flagged the enrichment as wrong. Hidden permanently.</summary>
    Rejected,
}
