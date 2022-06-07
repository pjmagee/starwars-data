using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Mongo;

[Serializable]
public class HyperLink
{
    [BsonElement, JsonInclude]
    public string Content { get; set; } = null!;
    
    [BsonElement, JsonInclude]
    public string Href { get; set; } = null!;
}