using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Tracks which pages have been processed by the relationship graph builder.
/// </summary>
public class RelationshipCrawlState
{
    /// <summary>PageId — matches the _id in the Pages collection.</summary>
    [BsonId]
    public int PageId { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Infobox type of this page (e.g. Character, Battle).</summary>
    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public CrawlStatus Status { get; set; } = CrawlStatus.Pending;

    /// <summary>Number of edges extracted from this page.</summary>
    [BsonElement("edgesExtracted")]
    public int EdgesExtracted { get; set; }

    [BsonElement("processedAt")]
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Extraction prompt/model version — allows re-crawl with improved prompts.</summary>
    [BsonElement("version")]
    public int Version { get; set; } = 1;

    [BsonElement("error")]
    public string? Error { get; set; }
}

public enum CrawlStatus
{
    Pending,
    Processing,
    Completed,
    Skipped,
    Failed
}
