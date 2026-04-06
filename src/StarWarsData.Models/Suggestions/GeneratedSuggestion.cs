using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// A dynamically generated example question shown on the Ask page empty state.
/// Produced by <c>DynamicQuestionGeneratorService</c> from live KG data and cached
/// in <c>suggestions.examples</c>. Each document is one fully-rendered prompt.
///
/// Tagged with <see cref="Continuity"/> and <see cref="Realm"/> so the frontend can
/// match the currently active global filter (Canon/Legends + Star Wars/Real world).
/// </summary>
public class GeneratedSuggestion
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    /// <summary>Target Ask visualization mode: chart, graph, table, data_table, timeline, infobox, text.</summary>
    [BsonElement("mode")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>Stable identifier of the template variant that produced this prompt (for dedup / debugging).</summary>
    [BsonElement("shape")]
    public string Shape { get; set; } = string.Empty;

    /// <summary>The rendered prompt shown to the user.</summary>
    [BsonElement("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Canon/Legends classification of the entities this prompt touches.
    /// Canon = every referenced entity is Canon. Legends = every referenced entity is Legends.
    /// Both = mixes the two continuities (so should match a null / "Both" global filter).
    /// </summary>
    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Canon;

    /// <summary>
    /// Star Wars (in-universe) vs Real (publications, filming, meta). Most prompts are
    /// Starwars; the generator also emits a small pool of Real prompts driven by
    /// TemporalFacets with the "publication.*" semantic role.
    /// </summary>
    [BsonElement("realm")]
    [BsonRepresentation(BsonType.String)]
    public Realm Realm { get; set; } = Realm.Starwars;

    /// <summary>PageIds of entities referenced in this prompt — used for dedup and click-through targeting.</summary>
    [BsonElement("entityIds")]
    public List<int> EntityIds { get; set; } = [];

    [BsonElement("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
