using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A single temporal data point on a knowledge graph node.
/// Preserves the semantic meaning of the date (born vs founded vs began)
/// and the calendar system (galactic BBY/ABY vs real-world CE).
/// </summary>
public class TemporalFacet
{
    /// <summary>Original infobox field name, e.g. "Born", "Date established", "Release date".</summary>
    [BsonElement("field")]
    public string Field { get; set; } = "";

    /// <summary>
    /// Semantic dimension and role. Format: "{dimension}.{role}".
    /// Dimensions: lifespan, conflict, construction, creation, institutional, publication.
    /// Roles vary by dimension: start, end, point, reorganized, restored, fragmented, suspended, etc.
    /// Examples: "lifespan.start", "conflict.end", "institutional.reorganized", "publication.release".
    /// </summary>
    [BsonElement("semantic")]
    public string Semantic { get; set; } = "";

    /// <summary>Calendar system: "galactic" (BBY/ABY sort-key), "real" (CE year), or "unknown".</summary>
    [BsonElement("calendar")]
    public string Calendar { get; set; } = "";

    /// <summary>
    /// Parsed year. For galactic: sort-key (negative=BBY, positive=ABY).
    /// For real: CE year (e.g. 1959, 2017).
    /// Null if unparseable (vague text like "During the Clone Wars").
    /// </summary>
    [BsonElement("year")]
    [BsonIgnoreIfNull]
    public int? Year { get; set; }

    /// <summary>Original text from infobox, preserved for display.</summary>
    [BsonElement("text")]
    public string Text { get; set; } = "";

    /// <summary>Sequence position within a lifecycle chain (0-based).</summary>
    [BsonElement("order")]
    public int Order { get; set; }
}
