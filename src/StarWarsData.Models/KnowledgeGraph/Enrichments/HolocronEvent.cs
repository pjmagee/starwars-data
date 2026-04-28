using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Append-only audit log entry for a Holocron state transition. One document per event.
/// Stored in <c>kg.events</c>. Never mutated — supersession or rejection of an enrichment
/// produces a new event, it does not edit the original.
///
/// This is the collection the frontend changelog page (Stage D) reads from.
///
/// See <c>eng/design/018-kg-enrichments-architecture.md</c>.
/// </summary>
public sealed class HolocronEvent
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("eventType")]
    [BsonRepresentation(BsonType.String)]
    public HolocronEventType EventType { get; set; }

    /// <summary>Pointer to the <see cref="NodeEnrichment"/> or <see cref="EdgeEnrichment"/> the event refers to.</summary>
    [BsonElement("enrichmentId")]
    [BsonIgnoreIfNull]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? EnrichmentId { get; set; }

    /// <summary>Target node PageId — present for node enrichments and node-scoped events.</summary>
    [BsonElement("pageId")]
    [BsonIgnoreIfDefault]
    public int PageId { get; set; }

    /// <summary>Target field path — present for node enrichments.</summary>
    [BsonElement("fieldPath")]
    [BsonIgnoreIfNull]
    public string? FieldPath { get; set; }

    /// <summary>Edge identity — present for edge-enrichment events.</summary>
    [BsonElement("fromId")]
    [BsonIgnoreIfDefault]
    public int FromId { get; set; }

    [BsonElement("toId")]
    [BsonIgnoreIfDefault]
    public int ToId { get; set; }

    [BsonElement("label")]
    [BsonIgnoreIfNull]
    public string? Label { get; set; }

    /// <summary>Human-readable summary, surfaced verbatim in the changelog UI.</summary>
    [BsonElement("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>What initiated this event: <c>"scheduled"</c>, <c>"manual"</c>, <c>"phase1-staleness"</c>.</summary>
    [BsonElement("triggeredBy")]
    public string TriggeredBy { get; set; } = string.Empty;

    [BsonElement("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>
    /// The <c>kg.enrichment_jobs._id</c> that emitted this event. Empty for events
    /// from the legacy synchronous path. Used together with the partial unique
    /// index <c>{jobId, enrichmentId}</c> from migration 0014 to make Apply replays
    /// idempotent for the audit log alongside the enrichment collections.
    /// </summary>
    [BsonElement("jobId")]
    [BsonIgnoreIfNull]
    public string? JobId { get; set; }
}
