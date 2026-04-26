using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One agent-produced addition or refinement to a single field on a <see cref="GraphNode"/>.
/// Stored in the <c>kg.enrichments</c> collection — never written by Phase 1.
/// Joined into <c>kg.nodes.enriched</c> at read time for active enrichments whose
/// <see cref="ContentHashAtCreation"/> still matches the source node's current hash.
///
/// See <c>eng/design/018-kg-enrichments-architecture.md</c>.
/// </summary>
public sealed class NodeEnrichment
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>Target node's <c>_id</c> (matches <c>GraphNode.PageId</c>).</summary>
    [BsonElement("pageId")]
    public int PageId { get; set; }

    /// <summary>Dotted path to the field this enrichment targets (e.g. <c>"properties.affiliations"</c>, <c>"temporalFacets"</c>).</summary>
    [BsonElement("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    [BsonElement("operation")]
    [BsonRepresentation(BsonType.String)]
    public EnrichmentOperation Operation { get; set; }

    /// <summary>The enrichment payload. Shape depends on <see cref="FieldPath"/> — typically a string, list, or facet doc.</summary>
    [BsonElement("value")]
    public BsonValue Value { get; set; } = BsonNull.Value;

    /// <summary>One-sentence statement of what the agent is asserting about the node.</summary>
    [BsonElement("claim")]
    public string Claim { get; set; } = string.Empty;

    [BsonElement("evidence")]
    public List<EnrichmentEvidence> Evidence { get; set; } = [];

    /// <summary>Free-form summary of the agent's reasoning. Surfaced in the changelog UI.</summary>
    [BsonElement("llmReasoning")]
    [BsonIgnoreIfNull]
    public string? LlmReasoning { get; set; }

    /// <summary>The source <see cref="GraphNode.ContentHash"/> at the time this enrichment was made. Drives staleness detection.</summary>
    [BsonElement("contentHashAtCreation")]
    public string ContentHashAtCreation { get; set; } = string.Empty;

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Active;

    /// <summary>Set on superseded enrichments — points at the active replacement.</summary>
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
