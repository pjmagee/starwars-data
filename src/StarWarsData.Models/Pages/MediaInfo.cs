using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class MediaInfo
{
    [BsonElement("title")]
    public required string Title { get; set; }

    [BsonElement("url")]
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [BsonElement("caption")]
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [BsonElement("altText")]
    [JsonPropertyName("altText")]
    public string? AltText { get; set; }

    [BsonElement("size")]
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
