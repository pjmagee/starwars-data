using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A node in the knowledge graph. Represents a single entity (character, planet, ship, etc.)
/// with scalar properties and typed relationships to other nodes.
/// Built deterministically from infobox data — no LLM needed.
/// </summary>
public class GraphNode
{
    /// <summary>PageId — matches the _id in the Pages collection.</summary>
    [BsonId]
    public int PageId { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Infobox template type (e.g. Character, CelestialBody, Government).</summary>
    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    /// <summary>Scalar properties (key → values). E.g. { "height": ["1.72 m"], "eye_color": ["Blue"] }</summary>
    [BsonElement("properties")]
    public Dictionary<string, List<string>> Properties { get; set; } = new();

    [BsonElement("imageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("wikiUrl")]
    public string? WikiUrl { get; set; }

    /// <summary>Content hash from the Page at time of processing.</summary>
    [BsonElement("contentHash")]
    public string? ContentHash { get; set; }

    [BsonElement("processedAt")]
    public DateTime ProcessedAt { get; set; }
}
