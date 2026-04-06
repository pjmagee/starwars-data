namespace StarWarsData.Models.Entities;

/// <summary>
/// Unified search result shape returned by every search strategy (keyword, semantic, hybrid).
/// Consumed by <c>SearchController</c>, <c>MapService</c>, and the GraphRAG agent toolkit.
/// </summary>
public class SearchHit
{
    public int PageId { get; set; }
    public string Title { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Section { get; set; } = "";
    public string WikiUrl { get; set; } = "";
    public string Type { get; set; } = "";
    public string Continuity { get; set; } = "";
    public string Realm { get; set; } = "";
    public string Text { get; set; } = "";
    public double Score { get; set; }

    /// <summary>Constructs a deep link to the specific section within the wiki page.</summary>
    public string SectionUrl => !string.IsNullOrEmpty(WikiUrl) && !string.IsNullOrEmpty(Section) ? $"{WikiUrl}#{Section}" : WikiUrl;
}
