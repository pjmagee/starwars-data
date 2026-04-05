namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="TemporalFacet"/> embedded document stored
/// inside <see cref="GraphNode.TemporalFacets"/>. These match the <c>[BsonElement("…")]</c>
/// attributes on the model's properties.
/// </summary>
public static class TemporalFacetBsonFields
{
    public const string Field = "field";
    public const string Semantic = "semantic";
    public const string Calendar = "calendar";
    public const string Year = "year";
    public const string Text = "text";
    public const string Order = "order";
}
