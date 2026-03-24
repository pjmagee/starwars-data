using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A single directed edge in the relationship graph.
/// Each relationship is stored as TWO edges (forward + reverse) sharing the same PairId,
/// enabling efficient $graphLookup traversal in either direction.
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
    /// Directed relationship label from source to target, e.g. "employs", "parent_of", "born_on".
    /// Must match a canonical label in the label registry.
    /// </summary>
    [BsonElement("label")]
    public string Label { get; set; } = string.Empty;

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

    /// <summary>Shared between forward/reverse edge pair for dedup/cleanup.</summary>
    [BsonElement("pairId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string PairId { get; set; } = null!;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
