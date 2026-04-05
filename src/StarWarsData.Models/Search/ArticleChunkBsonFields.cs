namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="ArticleChunk"/> document in the
/// <c>search.chunks</c> collection. These match the values on the <c>[BsonElement("…")]</c>
/// attributes.
/// </summary>
public static class ArticleChunkBsonFields
{
    public const string PageId = "pageId";
    public const string Title = "title";
    public const string Heading = "heading";
    public const string WikiUrl = "wikiUrl";
    public const string Section = "section";
    public const string ChunkIndex = "chunkIndex";
    public const string Text = "text";
    public const string Type = "type";
    public const string Continuity = "continuity";
    public const string Universe = "universe";
    public const string Embedding = "embedding";
    public const string CreatedAt = "createdAt";
}
