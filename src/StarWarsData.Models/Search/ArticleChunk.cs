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

    [BsonElement("realm")]
    [BsonRepresentation(BsonType.String)]
    public Realm Realm { get; set; } = Realm.Unknown;

    /// <summary>The embedding vector (text-embedding-3-small, 1536 dimensions).</summary>
    [BsonElement("embedding")]
    public float[] Embedding { get; set; } = [];

    /// <summary>
    /// Wookieepedia URLs this chunk's article links to via <c>&lt;a href&gt;</c> tags
    /// in the rendered HTML stored in <see cref="Text"/>. Deduped, case-sensitive (URLs
    /// are case-sensitive on Fandom). Multikey-indexed via the
    /// <c>0012-extract-chunk-links</c> migration so the Holocron agent can efficiently
    /// answer "which chunks reference page X" — the canonical "what-links-here" signal,
    /// stronger than infobox-edge ancestry or vector similarity.
    /// </summary>
    [BsonElement("links")]
    public List<string> Links { get; set; } = [];

    /// <summary>
    /// SHA256 hex digest of <see cref="Text"/> at chunk-write time. Used by the Holocron
    /// async pipeline (Design-020) to detect chunk-content changes between enrichment
    /// runs — when re-enhancing a node, chunks whose hash matches the per-(node, chunk)
    /// ledger entry are skipped, so unchanged content is never re-processed.
    /// Backfilled across the existing corpus by migration 0013-chunk-content-hash.
    /// </summary>
    [BsonElement("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
