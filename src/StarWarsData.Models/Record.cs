using System.Text.Json.Serialization;
using System.Web;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models;


[Serializable]
[BsonIgnoreExtraElements]
public class NoRelationshipRecord
{
    [BsonIgnore] public string PageTitle => HttpUtility.UrlDecode(PageUrl!.Split(new[] { "/wiki/" }, StringSplitOptions.None).Last()).Replace("_", " ");
    [BsonIgnore] public string Template => TemplateUrl.Split(':').Last();
    
    [BsonId, BsonElement("_id")] public int PageId { get; set; }
    [BsonElement] public string PageUrl { get; set; } = null!;
    [BsonElement] public string TemplateUrl { get; set; } = null!;
    [BsonElement] public string? ImageUrl { get; set; }
    [BsonElement] public List<InfoboxProperty> Data { get; set; } = new(); 
}

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

    public Record()
    {
        
    }

    public string ToString(string format)
    {
        if (format.Equals("Search"))
        {
            return
                string.Join(" ", TemplateUrl, PageUrl, PageId) +
                string.Join(" ", Data.Select(d => d.Label)) +
                string.Join(" ", Data.SelectMany(d => d.Values)) +
                string.Join(" ", Data.SelectMany(d => d.Links.Select(l => l.Content)));
        }

        return base.ToString();
    }
}