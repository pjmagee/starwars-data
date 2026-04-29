using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One row per unique post-dedup proposal emitted by the Holocron extractor, with the
/// outcome stamped at every pipeline stage. Lives in <c>kg.holocron_audits</c>.
///
/// <para>
/// Lifecycle: the consolidator inserts each row at consolidation time with a terminal
/// outcome (<c>rejected_preflight_*</c>, <c>rejected_evidence</c>) for proposals it
/// rejects, or <c>pending_verifier</c> for ones it forwards. The verifier upserts on
/// natural key to set <see cref="Outcome"/> to <c>verifier_accepted</c> or
/// <c>rejected_verifier</c>. The apply step finalises survivors as <c>applied</c>.
/// </para>
///
/// <para>
/// Natural key: <c>(jobId, kind, fromId, toId, label, fieldPath)</c>. Values that aren't
/// applicable for a given <see cref="Kind"/> (e.g. <c>fieldPath</c> on an edge proposal)
/// are <see cref="BsonNull"/>. The <see cref="Decisions"/> array preserves the
/// chronological per-stage trail; <see cref="Outcome"/> is the terminal denormalised
/// state for fast filtering.
/// </para>
/// </summary>
public sealed class HolocronAudit
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>The <c>kg.enrichment_jobs._id</c> that produced this proposal.</summary>
    [BsonElement("jobId")]
    public string JobId { get; set; } = string.Empty;

    /// <summary>Target node for the run (the focal entity Holocron is enhancing).</summary>
    [BsonElement("pageId")]
    public int PageId { get; set; }

    /// <summary>One of <c>"annotate"</c>, <c>"fillgap"</c>, <c>"add"</c>, <c>"property"</c>.</summary>
    [BsonElement("kind")]
    public string Kind { get; set; } = string.Empty;

    [BsonElement("fromId"), BsonIgnoreIfNull]
    public int? FromId { get; set; }

    [BsonElement("toId"), BsonIgnoreIfNull]
    public int? ToId { get; set; }

    [BsonElement("label"), BsonIgnoreIfNull]
    public string? Label { get; set; }

    [BsonElement("fieldPath"), BsonIgnoreIfNull]
    public string? FieldPath { get; set; }

    [BsonElement("values"), BsonIgnoreIfNull]
    public List<string>? Values { get; set; }

    [BsonElement("claim")]
    public string Claim { get; set; } = string.Empty;

    [BsonElement("reasoning"), BsonIgnoreIfNull]
    public string? Reasoning { get; set; }

    [BsonElement("evidence")]
    public List<EnrichmentEvidence> Evidence { get; set; } = [];

    [BsonElement("fromYear"), BsonIgnoreIfNull]
    public int? FromYear { get; set; }

    [BsonElement("toYear"), BsonIgnoreIfNull]
    public int? ToYear { get; set; }

    /// <summary>
    /// Terminal denormalised state. One of: <c>pending_verifier</c>, <c>applied</c>,
    /// <c>verifier_accepted</c> (only briefly — apply promotes to <c>applied</c>),
    /// <c>rejected_verifier</c>, <c>rejected_evidence</c>, <c>rejected_preflight_hedge</c>,
    /// <c>rejected_preflight_target_type</c>, <c>rejected_preflight_dup_pair</c>,
    /// <c>rejected_preflight_fieldpath</c>, <c>rejected_preflight_blocklist</c>,
    /// <c>rejected_preflight_other</c>.
    /// </summary>
    [BsonElement("outcome")]
    public string Outcome { get; set; } = "pending_verifier";

    [BsonElement("outcomeReason"), BsonIgnoreIfNull]
    public string? OutcomeReason { get; set; }

    [BsonElement("decisions")]
    public List<HolocronAuditDecision> Decisions { get; set; } = [];

    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One stage's verdict in a <see cref="HolocronAudit"/>'s lifecycle. The
/// <see cref="Stage"/> identifies which executor wrote the decision; <see cref="Outcome"/>
/// is the local verdict at that stage (which may differ from the final terminal outcome
/// recorded on the audit itself).
/// </summary>
public sealed record HolocronAuditDecision(
    [property: BsonElement("stage")] string Stage,
    [property: BsonElement("outcome")] string Outcome,
    [property: BsonElement("reason")] string Reason,
    [property: BsonElement("at")] DateTime At
);
