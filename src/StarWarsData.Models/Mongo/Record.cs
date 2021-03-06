using System.Text.Json.Serialization;
using System.Web;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Mongo;

[Serializable]
public class Record
{
    [JsonIgnore, BsonIgnore] public string PageTitle => HttpUtility.UrlDecode(PageUrl!.Split(new[] { "/wiki/" }, StringSplitOptions.None).Last()).Replace("_", " ");
    [JsonIgnore, BsonIgnore] public string Template => TemplateUrl.Split(':').Last().Replace("_infobox", string.Empty);
    [JsonInclude, BsonId, BsonElement("_id")] public int PageId { get; set; }
    [JsonInclude, BsonElement] public string PageUrl { get; set; } = null!;
    [JsonInclude, BsonElement] public string TemplateUrl { get; set; } = null!;
    [JsonInclude, BsonElement] public string? ImageUrl { get; set; }
    [JsonInclude, BsonElement] public List<InfoboxProperty> Data { get; set; } = new();
    [JsonInclude, BsonElement] public List<Relationship> Relationships { get; set; } = new();
    [JsonIgnore, BsonIgnore] public bool ShowRelationships { get; set; } = false;
}