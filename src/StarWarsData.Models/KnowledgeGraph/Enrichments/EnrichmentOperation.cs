namespace StarWarsData.Models.Entities;

/// <summary>
/// Kind of enrichment the Holocron agent is making to a node or edge.
/// Drives the temporal-precedence policy (see Design-018): <see cref="Add"/>
/// operations are applied to the merged view; <see cref="Refine"/> operations
/// are surfaced as suggestions for human review and are NOT auto-applied.
/// </summary>
public enum EnrichmentOperation
{
    /// <summary>Property / facet / edge didn't exist in the infobox extraction; agent created it.</summary>
    Add,

    /// <summary>Property existed but the agent narrowed or corrected the value (suggestion-only).</summary>
    Refine,

    /// <summary>Property existed as a list and the agent appended additional entries.</summary>
    Augment,
}
