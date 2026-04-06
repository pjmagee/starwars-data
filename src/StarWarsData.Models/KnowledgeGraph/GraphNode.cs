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

    [BsonElement("realm")]
    [BsonRepresentation(BsonType.String)]
    public Realm Realm { get; set; } = Realm.Unknown;

    /// <summary>Scalar properties (key → values). E.g. { "height": ["1.72 m"], "eye_color": ["Blue"] }</summary>
    [BsonElement("properties")]
    public Dictionary<string, List<string>> Properties { get; set; } = new();

    [BsonElement("imageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("wikiUrl")]
    public string? WikiUrl { get; set; }

    // ── Temporal lifecycle (optional) ──

    /// <summary>Sort-key year when this entity came into existence (negative = BBY, positive = ABY).</summary>
    [BsonElement("startYear")]
    [BsonIgnoreIfNull]
    public int? StartYear { get; set; }

    /// <summary>Sort-key year when this entity ceased to exist.</summary>
    [BsonElement("endYear")]
    [BsonIgnoreIfNull]
    public int? EndYear { get; set; }

    /// <summary>Original date text from the infobox (e.g. "22 BBY", "c. 5 BBY (unofficially)").</summary>
    [BsonElement("startDateText")]
    [BsonIgnoreIfNull]
    public string? StartDateText { get; set; }

    /// <summary>Original date text for end/dissolution.</summary>
    [BsonElement("endDateText")]
    [BsonIgnoreIfNull]
    public string? EndDateText { get; set; }

    /// <summary>All temporal facets, preserving semantic meaning and lifecycle order.</summary>
    [BsonElement("temporalFacets")]
    public List<TemporalFacet> TemporalFacets { get; set; } = [];

    /// <summary>
    /// Precomputed transitive closures of tree- or DAG-shaped relationship labels
    /// (see <c>HierarchyRegistry</c>). Key is the lineage name (e.g. <c>apprentice_of</c>,
    /// <c>ancestors</c>, <c>in_region</c>); value is the ordered list of <c>PageId</c>s
    /// reachable from this node by walking that lineage from nearest to farthest.
    ///
    /// Populated during Phase 5 post-processing. Empty subdocument for nodes that don't
    /// participate in any registered lineage. Backed by a wildcard index on <c>lineages.$**</c>
    /// so membership queries like <c>{"lineages.apprentice_of": targetId}</c> are O(1).
    /// See ADR-003 Gap 1 and Design-008.
    /// </summary>
    [BsonElement("lineages")]
    public Dictionary<string, List<int>> Lineages { get; set; } = [];

    /// <summary>Content hash from the Page at time of processing.</summary>
    [BsonElement("contentHash")]
    public string? ContentHash { get; set; }

    [BsonElement("processedAt")]
    public DateTime ProcessedAt { get; set; }
}
