using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace StarWarsData.Models.Entities;

public class Page
{
    [BsonId]
    [BsonElement("pageId")]
    public required int PageId { get; set; }

    [BsonElement("title")]
    public required string Title { get; set; }
    
    [BsonElement("infobox")]
    public List<InfoboxProperty> Infobox { get; set; } = new();
    
    [BsonElement("sections")]
    public required List<ArticleSection> Sections { get; set; }
    
    [BsonElement("categories")]
    public required List<string> Categories { get; set; }
    
    [BsonElement("images")]
    public required List<MediaInfo> Images { get; set; }
    
    [BsonElement("wikiUrl")]
    public string? WikiUrl { get; set; }
    
    [BsonElement("lastModified")]
    public DateTime LastModified { get; set; }
    
    [BsonElement("summary")]
    public string? Summary { get; set; }
}

public class ArticleSection
{
    [BsonElement("heading")]
    [JsonPropertyName("heading")]
    public required string Heading { get; set; }
    
    [BsonElement("content")]
    [JsonPropertyName("content")]
    public required string Content { get; set; }
    
    [BsonElement("plainText")]
    [JsonPropertyName("plainText")]
    public required string PlainText { get; set; }
    
    [BsonElement("level")]
    [JsonPropertyName("level")]
    public int Level { get; set; }
    
    [BsonElement("links")]
    [JsonPropertyName("links")]
    public List<string> Links { get; set; } = new();
}