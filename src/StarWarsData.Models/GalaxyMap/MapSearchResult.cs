namespace StarWarsData.Models.Queries;

public class MapSearchResult
{
    public string GridKey { get; set; } = null!;
    public int PageId { get; set; }
    public string MatchedName { get; set; } = null!;
    public string? Template { get; set; }
    public string MatchType { get; set; } = null!; // "direct", "linked", "semantic", "semantic-linked"
    public string? LinkedVia { get; set; } // e.g. "Homeworld of Luke Skywalker"
    public int? SourcePageId { get; set; } // For linked results: the entity that referenced the location
    public string? SourceName { get; set; } // For linked results: name of the referencing entity
    public string? Snippet { get; set; } // Semantic search: text excerpt
    public double? Score { get; set; } // Semantic search: relevance score
}
