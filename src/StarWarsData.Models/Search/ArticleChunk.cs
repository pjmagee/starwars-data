using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A semantically meaningful chunk of a wiki article, stored with its embedding vector
/// for vector search retrieval. Each chunk corresponds to a markdown section (or sub-section)
/// of the original page content.
/// </summary>
public class ArticleChunk
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    /// <summary>PageId of the source page (matches Page._id).</summary>
    [BsonElement("pageId")]
    public int PageId { get; set; }

    /// <summary>Page title for display/context.</summary>
    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Markdown section heading this chunk came from (e.g. "Biography", "Early life").</summary>
    [BsonElement("heading")]
    public string Heading { get; set; } = string.Empty;

    /// <summary>Wookieepedia URL for the source page.</summary>
    [BsonElement("wikiUrl")]
    public string WikiUrl { get; set; } = string.Empty;

    /// <summary>URL-friendly section anchor derived from heading (spaces → underscores).</summary>
    [BsonElement("section")]
    public string Section { get; set; } = string.Empty;

    /// <summary>Zero-based index of this chunk within the page.</summary>
    [BsonElement("chunkIndex")]
    public int ChunkIndex { get; set; }

    /// <summary>The chunk text (markdown stripped of boilerplate, with title/heading prefix for embedding).</summary>
    [BsonElement("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Infobox type of the source page (e.g. Character, Planet).</summary>
    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("universe")]
    [BsonRepresentation(BsonType.String)]
    public Universe Universe { get; set; } = Universe.Unknown;

    /// <summary>The embedding vector (text-embedding-3-small, 1536 dimensions).</summary>
    [BsonElement("embedding")]
    public float[] Embedding { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
