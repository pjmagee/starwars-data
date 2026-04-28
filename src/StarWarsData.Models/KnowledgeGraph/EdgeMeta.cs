using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Optional structured metadata attached to a <see cref="RelationshipEdge"/>.
/// Populated by the infobox ETL when an edge is parsed from a value with a
/// qualifier (the text inside parentheses) or a raw source string. Edges
/// without a qualifier leave this null so the document stays compact.
/// </summary>
[BsonIgnoreExtraElements]
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

    /// <summary>
    /// The originating infobox field label this edge was extracted from
    /// (e.g. <c>"Affiliation(s)"</c>, <c>"commanders1"</c>, <c>"Sector capital"</c>).
    /// Stamped by the generic <c>NodeBuilderBase.Build</c> loop on every edge it
    /// emits so per-type <c>OnFinalize</c> overrides can branch on the source
    /// field cleanly — particularly Pattern B (per-side index encoding) and
    /// Pattern C (field-alias collapse) from Design-024. The human-readable
    /// <see cref="RelationshipEdge.Evidence"/> string is preserved for audit;
    /// this field is the structured counterpart for programmatic dispatch.
    /// </summary>
    [BsonElement("sourceFieldLabel"), BsonIgnoreIfNull]
    public string? SourceFieldLabel { get; set; }

    /// <summary>
    /// How the edge's <see cref="RelationshipEdge.FromYear"/> /
    /// <see cref="RelationshipEdge.ToYear"/> were determined. Distinguishes hard
    /// infobox-supplied bounds from soft lifecycle-fallback derivations so
    /// Holocron's FillGap pre-flight and the query-time merge layers can refine
    /// the soft ones without overwriting the hard ones. Absent (= Unknown) when
    /// both bounds are null or provenance pre-dates Design-021.
    /// Stored as the enum case-name string to match the convention on
    /// <c>EnrichmentStatus</c> / <c>EnrichmentOperation</c> — readable in
    /// mongosh and matched verbatim by Migration 0015's retroactive tagging.
    /// </summary>
    [BsonElement("boundsSource"), BsonIgnoreIfDefault]
    [BsonRepresentation(BsonType.String)]
    public EdgeBoundsSource BoundsSource { get; set; }

    /// <summary>
    /// 1-based belligerent side index for conflict-style relationships
    /// (Battle, Mission, Duel, War, Campaign, Event). Stamped by per-type
    /// <c>OnFinalize</c> overrides (Design-024 Phase B, Pattern B) when the
    /// edge originates from a numbered field (<c>commanders1..4</c>,
    /// <c>ppl1..4</c>, <c>unit1..4</c>, <c>side1..4</c>). Lets multi-belligerent
    /// queries answer "who commanded the Separatists at Geonosis" without
    /// joining through <c>belligerent</c> edges to identify the side.
    /// </summary>
    [BsonElement("sideIndex"), BsonIgnoreIfNull]
    public int? SideIndex { get; set; }
}
