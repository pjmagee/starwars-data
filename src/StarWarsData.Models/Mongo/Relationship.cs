using System.Text.Json.Serialization;
using System.Web;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Mongo;

[Serializable]
public class Relationship
{
    [JsonIgnore,  BsonIgnore] 
    public string PageTitle => HttpUtility.UrlDecode(PageUrl!.Split(["/wiki/"], StringSplitOptions.RemoveEmptyEntries).Last()).Replace("_", " ");

    [JsonIgnore,  BsonIgnore] 
    public string Template => TemplateUrl!.Split(':').LastOrDefault() ?? string.Empty;
    
    
    [JsonInclude, BsonElement] 
    public int PageId { get; set; }

    [JsonInclude, BsonElement] 
    public string PageUrl { get; set; } = null!;

    [JsonInclude, BsonElement] 
    public string TemplateUrl { get; set; } = null!;

    public Relationship()
    {
        
    }

    public Relationship(Loaded loaded)
    {
        this.PageId = loaded.Record.PageId;
        this.TemplateUrl = loaded.Record.TemplateUrl;
        this.PageUrl = loaded.Record.PageUrl;
    }
}