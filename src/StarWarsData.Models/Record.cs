using System.Text.Json.Serialization;
using System.Web;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models;

[Serializable]
public class Record
{
    [JsonIgnore, BsonIgnore] public string PageTitle => HttpUtility.UrlDecode(PageUrl!.Split("/wiki/").Last()).Replace("_", " ");
    [JsonIgnore, BsonIgnore] public string Template => TemplateUrl.Split(':').Last();
    [JsonInclude, BsonId, BsonElement("_id")] public int PageId { get; set; }
    [JsonInclude, BsonElement] public string PageUrl { get; set; } = null!;
    [JsonInclude, BsonElement] public string TemplateUrl { get; set; } = null!;
    [JsonInclude, BsonElement] public string? ImageUrl { get; set; }
    [JsonInclude, BsonElement] public List<InfoboxProperty> Data { get; set; } = new();
    [JsonInclude, BsonElement] public List<Relationship> Relationships { get; set; } = new();

    public Record()
    {
        
    }
}