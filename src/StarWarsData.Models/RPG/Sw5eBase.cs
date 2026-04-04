using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

/// <summary>
/// Common metadata fields shared by all SW5e collections.
/// </summary>
public abstract class Sw5eBase
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    [BsonElement("contentTypeEnum")]
    public int ContentTypeEnum { get; set; }

    [BsonElement("contentType")]
    public string ContentType { get; set; } = "";

    [BsonElement("contentSourceEnum")]
    public int ContentSourceEnum { get; set; }

    [BsonElement("contentSource")]
    public string ContentSource { get; set; } = "";

    [BsonElement("partitionKey")]
    public string? PartitionKey { get; set; }

    [BsonElement("rowKey")]
    public string? RowKey { get; set; }

    [BsonElement("timestamp")]
    public string? Timestamp { get; set; }

    [BsonElement("eTag")]
    public string? ETag { get; set; }
}
