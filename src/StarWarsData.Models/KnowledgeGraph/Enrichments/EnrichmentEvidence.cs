using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One piece of evidence cited by the Holocron agent in support of an enrichment.
/// Every enrichment must carry at least one piece of evidence pointing at a real
/// source (KG page, article chunk, or external URL); proposals failing this check
/// are dropped before being written.
/// </summary>
public sealed class EnrichmentEvidence
{
    /// <summary>PageId of the source KG node the agent drew from.</summary>
    [BsonElement("sourcePageId")]
    [BsonIgnoreIfDefault]
    public int SourcePageId { get; set; }

    /// <summary>Optional pointer to a specific <c>articleChunks</c> document for chunk-level citation.</summary>
    [BsonElement("chunkId")]
    [BsonIgnoreIfNull]
    public string? ChunkId { get; set; }

    /// <summary>Verbatim excerpt from the source supporting the claim. Capped at ~500 chars.</summary>
    [BsonElement("excerpt")]
    public string Excerpt { get; set; } = string.Empty;

    /// <summary>Cosine similarity, retrieval rank, or other signal. Optional.</summary>
    [BsonElement("relevanceScore")]
    [BsonIgnoreIfNull]
    public double? RelevanceScore { get; set; }
}
