using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical relationship label in the registry. Ensures LLM reuses consistent labels
/// instead of creating synonyms (e.g. "employs" vs "hires" vs "employer_of").
/// </summary>
public class RelationshipLabel
{
    /// <summary>Canonical label string, e.g. "employs".</summary>
    [BsonId]
    public string Label { get; set; } = null!;

    /// <summary>The reverse label, e.g. "employed_by".</summary>
    [BsonElement("reverse")]
    public string Reverse { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this relationship means.</summary>
    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Entity types typically on the source side, e.g. ["Character", "Organization"].</summary>
    [BsonElement("fromTypes")]
    public List<string> FromTypes { get; set; } = [];

    /// <summary>Entity types typically on the target side.</summary>
    [BsonElement("toTypes")]
    public List<string> ToTypes { get; set; } = [];

    /// <summary>How many edges use this label.</summary>
    [BsonElement("usageCount")]
    public int UsageCount { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
