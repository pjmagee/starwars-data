using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Optional structured metadata attached to a <see cref="RelationshipEdge"/>.
/// Populated by the infobox ETL when an edge is parsed from a value with a
/// qualifier (the text inside parentheses) or a raw source string. Edges
/// without a qualifier leave this null so the document stays compact.
/// </summary>
public sealed class EdgeMeta
{
    /// <summary>
    /// The parenthetical qualifier text, e.g. <c>"informal Jedi Master"</c> for
    /// <c>"Qui-Gon Jinn (informal Jedi Master)"</c>, or
    /// <c>"19 BBY–4 ABY, disbanded by Emperor Sheev Palpatine"</c> for a temporal+agent qualifier.
    /// </summary>
    [BsonElement("qualifier"), BsonIgnoreIfNull]
    public string? Qualifier { get; set; }

    /// <summary>
    /// The original verbatim value string from the infobox field, before parsing.
    /// Preserved so downstream consumers can re-parse or display the exact source.
    /// </summary>
    [BsonElement("rawValue"), BsonIgnoreIfNull]
    public string? RawValue { get; set; }

    /// <summary>
    /// Sequence within the source field's value list. Preserves ordering when a
    /// field has multiple values (e.g. multiple masters in order of training).
    /// </summary>
    [BsonElement("order"), BsonIgnoreIfNull]
    public int? Order { get; set; }
}
