using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One agent-produced addition or refinement to a single edge in <see cref="RelationshipEdge"/>.
/// Stored in the <c>kg.edge_enrichments</c> collection — never collides with Phase 1's
/// or Phase 6's writes to <c>kg.edges</c>. Joined into <c>kg.edges.enriched</c> at read time.
///
/// See <c>eng/design/018-kg-enrichments-architecture.md</c>.
/// </summary>
public sealed class EdgeEnrichment
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>Source node's PageId — matches <see cref="RelationshipEdge.FromId"/>.</summary>
    [BsonElement("fromId")]
    public int FromId { get; set; }

    /// <summary>Target node's PageId — matches <see cref="RelationshipEdge.ToId"/>.</summary>
    [BsonElement("toId")]
    public int ToId { get; set; }

    /// <summary>Canonical edge label — matches <see cref="RelationshipEdge.Label"/>.</summary>
    [BsonElement("label")]
    public string Label { get; set; } = string.Empty;

    [BsonElement("operation")]
    [BsonRepresentation(BsonType.String)]
    public EnrichmentOperation Operation { get; set; }

    /// <summary>
    /// Enrichment payload. For <see cref="EnrichmentOperation.Add"/> this carries the proposed
    /// edge attributes (weight, fromYear, toYear, meta). For <see cref="EnrichmentOperation.Refine"/>
    /// it carries only the fields being narrowed.
    /// </summary>
    [BsonElement("value")]
    public BsonDocument Value { get; set; } = new();

    [BsonElement("claim")]
    public string Claim { get; set; } = string.Empty;

    [BsonElement("evidence")]
    public List<EnrichmentEvidence> Evidence { get; set; } = [];

    [BsonElement("llmReasoning")]
    [BsonIgnoreIfNull]
    public string? LlmReasoning { get; set; }

    /// <summary>
    /// Concatenated <see cref="GraphNode.ContentHash"/> of <c>fromId</c> and <c>toId</c> nodes
    /// at the time this enrichment was made. Either side changing flips this enrichment to
    /// <see cref="EnrichmentStatus.Stale"/>.
    /// </summary>
    [BsonElement("contentHashAtCreation")]
    public string ContentHashAtCreation { get; set; } = string.Empty;

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Active;

    [BsonElement("supersededBy")]
    [BsonIgnoreIfNull]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SupersededBy { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("appliedAt")]
    [BsonIgnoreIfNull]
    public DateTime? AppliedAt { get; set; }

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;

    [BsonElement("modelId")]
    public string ModelId { get; set; } = string.Empty;
}
