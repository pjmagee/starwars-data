namespace StarWarsData.Models.Entities;

/// <summary>
/// Kind of enrichment the Holocron agent is making to a node or edge.
///
/// **v1 policy** (see <c>specs/018-kg-enrichments-architecture/spec.md</c>):
/// the original infobox extraction is the canonical truth foundation. Holocron
/// only adds context; it never contradicts or overwrites. The four permitted
/// ops are strictly additive:
///
/// - <see cref="Add"/>      — the field / edge did not exist; agent creates it from external evidence.
/// - <see cref="Augment"/>  — the field exists as a list; agent appends new items, deduped against existing values.
/// - <see cref="FillGap"/>  — the field exists but a sub-property is null (e.g. an edge with <c>fromYear: null</c>); agent fills the null only.
/// - <see cref="Annotate"/> — an edge exists with the right canonical label; agent attaches richer context
///   (role, qualifier, description) WITHOUT creating a parallel edge. Use this when the agent wants to say
///   "Obi-Wan's <c>affiliated_with</c> Galactic Republic edge had a specific role: Jedi General during the
///   Clone Wars" — the relationship is correct, just under-specified. Zero visual clutter in the graph
///   viewer; richer context surfaced on hover/expand.
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

    /// <summary>
    /// An edge exists with the correct canonical label; agent attaches role / qualifier / description
    /// context. Does NOT create a new edge — the existing one is the only edge between the pair.
    /// Used when the agent wants to enrich the relationship's narrative without introducing synonym
    /// labels (e.g. <c>member_of</c> next to <c>affiliated_with</c>) that would clutter the graph viewer
    /// and double-count the same fact in aggregations. The annotation surfaces in the merged view as
    /// supplementary metadata on the existing edge.
    /// </summary>
    Annotate,
}
