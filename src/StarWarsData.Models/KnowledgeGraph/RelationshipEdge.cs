using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A single directed edge in the relationship graph.
/// The deterministic infobox ETL (<see cref="InfoboxGraphService"/>) writes a single directed
/// edge per relationship; the reverse is synthesized at query time via the label registry.
/// The LLM-driven path (<see cref="AI.Toolkits.RelationshipAnalystToolkit"/>) writes forward +
/// reverse edges sharing the same <see cref="PairId"/> for dedup/cleanup.
/// Cached <c>fromName/fromType/toName/toType</c> are immutable per ETL run (Phase 5 is full-replace).
/// </summary>
public class RelationshipEdge
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    /// <summary>Source entity PageId.</summary>
    [BsonElement("fromId")]
    public int FromId { get; set; }

    [BsonElement("fromName")]
    public string FromName { get; set; } = string.Empty;

    /// <summary>Infobox type of the source entity (e.g. Character, Organization, Battle).</summary>
    [BsonElement("fromType")]
    public string FromType { get; set; } = string.Empty;

    /// <summary>Target entity PageId.</summary>
    [BsonElement("toId")]
    public int ToId { get; set; }

    [BsonElement("toName")]
    public string ToName { get; set; } = string.Empty;

    /// <summary>Infobox type of the target entity.</summary>
    [BsonElement("toType")]
    public string ToType { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized realm of the source entity, copied from the source node during ETL.
    /// Enables realm filtering as a simple edge-level match instead of a per-edge node lookup,
    /// and lets realm participate in <c>restrictSearchWithMatch</c> for <c>$graphLookup</c>.
    /// </summary>
    [BsonElement("fromRealm")]
    [BsonRepresentation(BsonType.String)]
    public Realm FromRealm { get; set; } = Realm.Unknown;

    /// <summary>Denormalized realm of the target entity. See <see cref="FromRealm"/>.</summary>
    [BsonElement("toRealm")]
    [BsonRepresentation(BsonType.String)]
    public Realm ToRealm { get; set; } = Realm.Unknown;

    /// <summary>
    /// Directed relationship label from source to target, e.g. "employs", "parent_of", "born_on".
    /// Must match a canonical label in the label registry.
    /// </summary>
    [BsonElement("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized reverse form of <see cref="Label"/> resolved from <c>FieldSemantics.Relationships</c>
    /// during Phase 5 ETL. Empty/null when the label has no registered reverse. Powers the
    /// <c>kg.edges.bidir</c> view, which uses this field to relabel edges in its reverse branch
    /// without a per-row lookup against <c>kg.labels</c>.
    /// </summary>
    [BsonElement("reverseLabel"), BsonIgnoreIfNull]
    public string? ReverseLabel { get; set; }

    /// <summary>LLM confidence in this relationship (0.0 - 1.0).</summary>
    [BsonElement("weight")]
    public double Weight { get; set; }

    /// <summary>Supporting evidence text snippet from the source article.</summary>
    [BsonElement("evidence")]
    public string Evidence { get; set; } = string.Empty;

    /// <summary>PageId of the article this relationship was extracted from.</summary>
    [BsonElement("sourcePageId")]
    public int SourcePageId { get; set; }

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    /// <summary>
    /// Shared between a forward/reverse edge pair written by the LLM extraction path for dedup/cleanup.
    /// Null on deterministic edges (InfoboxGraphService only writes one direction).
    /// </summary>
    [BsonElement("pairId"), BsonIgnoreIfNull]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PairId { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Temporal bounds (optional) ──

    /// <summary>
    /// Sort-key year when this relationship began (negative = BBY, positive = ABY).
    /// Null if unknown or the relationship has no temporal scope.
    /// </summary>
    [BsonElement("fromYear")]
    [BsonIgnoreIfNull]
    public int? FromYear { get; set; }

    /// <summary>
    /// Sort-key year when this relationship ended.
    /// Null if still active or unknown.
    /// </summary>
    [BsonElement("toYear")]
    [BsonIgnoreIfNull]
    public int? ToYear { get; set; }

    /// <summary>
    /// Optional structured metadata parsed from the source infobox value.
    /// Null for edges without a qualifier (keeps the document compact).
    /// </summary>
    [BsonElement("meta"), BsonIgnoreIfNull]
    public EdgeMeta? Meta { get; set; }
}
