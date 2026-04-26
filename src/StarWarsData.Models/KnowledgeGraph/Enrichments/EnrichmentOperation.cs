namespace StarWarsData.Models.Entities;

/// <summary>
/// Kind of enrichment the Holocron agent is making to a node or edge.
///
/// **v1 policy** (see <c>eng/design/018-kg-enrichments-architecture.md</c>):
/// the original infobox extraction is the canonical truth foundation. Holocron
/// only adds; it never contradicts or overwrites. The three permitted ops are
/// strictly additive:
///
/// - <see cref="Add"/>     — the field / edge did not exist; agent creates it from external evidence.
/// - <see cref="Augment"/> — the field exists as a list; agent appends new items, deduped against existing values.
/// - <see cref="FillGap"/> — the field exists but a sub-property is null (e.g. an edge with <c>fromYear: null</c>); agent fills the null only.
///
/// A "Refine" operation that would change a non-null value is explicitly forbidden in v1
/// — the wiki is the canonical source, agent enrichments polish around the edges only.
/// </summary>
public enum EnrichmentOperation
{
    /// <summary>Property / facet / edge didn't exist in the infobox extraction; agent created it.</summary>
    Add,

    /// <summary>Property exists as a list; agent appends additional entries, deduped against existing values.</summary>
    Augment,

    /// <summary>
    /// A sub-property on an existing entity is null (e.g. <c>edge.fromYear</c>, <c>node.endYear</c>);
    /// agent fills only the null fields. The read-side merge applies the fill iff the base is still null.
    /// </summary>
    FillGap,
}
