namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="GraphNode"/> document in <c>kg.nodes</c>.
/// These match the values on the <c>[BsonElement("…")]</c> attributes on each property.
/// </summary>
public static class GraphNodeBsonFields
{
    public const string Name = "name";
    public const string Type = "type";
    public const string Continuity = "continuity";
    public const string Universe = "universe";
    public const string Properties = "properties";
    public const string ImageUrl = "imageUrl";
    public const string WikiUrl = "wikiUrl";
    public const string StartYear = "startYear";
    public const string EndYear = "endYear";
    public const string StartDateText = "startDateText";
    public const string EndDateText = "endDateText";
    public const string TemporalFacets = "temporalFacets";
    public const string ContentHash = "contentHash";
    public const string ProcessedAt = "processedAt";

    // ── Nested paths into embedded TemporalFacet documents ──
    public const string TemporalFacetSemantic = TemporalFacets + "." + TemporalFacetBsonFields.Semantic;
    public const string TemporalFacetYear = TemporalFacets + "." + TemporalFacetBsonFields.Year;
    public const string TemporalFacetCalendar = TemporalFacets + "." + TemporalFacetBsonFields.Calendar;
    public const string TemporalFacetText = TemporalFacets + "." + TemporalFacetBsonFields.Text;
}
