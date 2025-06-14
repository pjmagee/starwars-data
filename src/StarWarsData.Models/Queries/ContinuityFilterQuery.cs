using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Queries;

/// <summary>
/// Query parameters for filtering by continuity
/// </summary>
public class ContinuityFilterQuery
{
    /// <summary>
    /// The continuity to filter by. If null, no continuity filtering is applied.
    /// </summary>
    public Continuity? Continuity { get; set; }

    /// <summary>
    /// Gets whether this query includes the specified continuity.
    /// When Continuity is Both, it includes all continuities.
    /// When Continuity is null, it includes all continuities.
    /// </summary>
    /// <param name="continuity">The continuity to check</param>
    /// <returns>True if the continuity should be included</returns>
    public bool IncludesContinuity(Continuity continuity)
    {
        return Continuity is null or Entities.Continuity.Both || Continuity == continuity;
    }
}
