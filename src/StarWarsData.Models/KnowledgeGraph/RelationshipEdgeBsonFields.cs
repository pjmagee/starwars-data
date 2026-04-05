namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="RelationshipEdge"/> document in
/// <c>kg.edges</c>. These match the values on the <c>[BsonElement("…")]</c> attributes.
/// </summary>
public static class RelationshipEdgeBsonFields
{
    public const string FromId = "fromId";
    public const string FromName = "fromName";
    public const string FromType = "fromType";
    public const string ToId = "toId";
    public const string ToName = "toName";
    public const string ToType = "toType";
    public const string Label = "label";
    public const string Weight = "weight";
    public const string Evidence = "evidence";
    public const string SourcePageId = "sourcePageId";
    public const string Continuity = "continuity";
    public const string PairId = "pairId";
    public const string CreatedAt = "createdAt";
    public const string FromYear = "fromYear";
    public const string ToYear = "toYear";
}
