using System.Web;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class Relationship
{
    [BsonIgnore]
    public string PageTitle =>
        HttpUtility
            .UrlDecode(PageUrl!.Split(["/wiki/"], StringSplitOptions.RemoveEmptyEntries).Last())
            .Replace("_", " ");

    [BsonIgnore]
    public string Template => TemplateUrl!.Split(':').LastOrDefault() ?? string.Empty;

    [BsonElement]
    public int PageId { get; set; }

    [BsonElement]
    public string PageUrl { get; set; } = null!;

    [BsonElement]
    public string TemplateUrl { get; set; } = null!;

    public Relationship() { }

    public Relationship(Loaded loaded)
    {
        this.PageId = loaded.Record.PageId;
        this.TemplateUrl = loaded.Record.TemplateUrl;
        this.PageUrl = loaded.Record.PageUrl;
    }
}
