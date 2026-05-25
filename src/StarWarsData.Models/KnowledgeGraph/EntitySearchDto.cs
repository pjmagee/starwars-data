namespace StarWarsData.Models.Queries;

public class EntitySearchDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Continuity { get; set; }

    /// <summary>
    /// Populated by the /families/search endpoint when this Family hit matched
    /// via one of its member Characters rather than the family's own name —
    /// e.g. the user typed "Anakin" and Skywalker family came back with
    /// <c>MatchedVia = "Anakin Skywalker"</c>. Lets the autocomplete surface
    /// the connection so the user knows why they're seeing this family.
    /// Null for direct name matches and for other endpoints that ignore it.
    /// </summary>
    public string? MatchedVia { get; set; }
}
