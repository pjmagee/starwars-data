namespace StarWarsData.Models.Queries;

/// <summary>
/// One citation card's worth of navigation options for a KG node, returned by
/// <c>POST /api/citations/resolve</c> and rendered by <c>CitationCard.razor</c>.
///
/// Agents pass a list of page-ids; the resolver fills in name/kind/links so
/// the agent never has to know about URL shapes. See Design-030 for the full
/// rationale and Phase rollout plan.
/// </summary>
public sealed record CitationReference(int PageId, string Name, string Kind, string? Continuity, string? ImageUrl, CitationLinks Links);

/// <summary>
/// The set of in-site and external surfaces the user can navigate to for a
/// given KG node. Each property is null when the surface doesn't apply —
/// e.g. <c>GalaxyMap</c> is null for non-spatial entities, <c>Holocron</c>
/// is null when no Holocron run exists. The frontend renders one button per
/// non-null entry.
///
/// All paths are relative (or absolute external) — callers should treat them
/// opaquely and never construct alternates.
/// </summary>
public sealed record CitationLinks(string? Wiki, string? GraphExplorer, string? GalaxyMap, string? Timeline, string? Holocron);
