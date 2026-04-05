namespace StarWarsData.Models.Entities;

/// <summary>
/// Unified search result shape returned by both semantic (vector) and keyword search.
/// Consumed by the API search endpoints, the MapService location-BFS, and the
/// KeywordSearchService fallback path.
/// </summary>
public class SemanticSearchResult
{
    public int PageId { get; set; }
    public string Title { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Section { get; set; } = "";
    public string WikiUrl { get; set; } = "";
    public string Type { get; set; } = "";
    public string Continuity { get; set; } = "";
    public string Universe { get; set; } = "";
    public string Text { get; set; } = "";
    public double Score { get; set; }

    /// <summary>Constructs a deep link to the specific section within the wiki page.</summary>
    public string SectionUrl => !string.IsNullOrEmpty(WikiUrl) && !string.IsNullOrEmpty(Section) ? $"{WikiUrl}#{Section}" : WikiUrl;
}
