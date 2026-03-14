using System.Web;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class Relationship
{

    [BsonElement]
    public int PageId { get; set; }

    [BsonElement]
    public string WikiUrl { get; set; } = null!;    [BsonElement]
    public string Template { get; set; } = null!;

    [BsonElement]
    public string? PageTitle { get; set; }

    public Relationship() { }    public Relationship(Loaded loaded)
    {
        this.PageId = loaded.Record?.PageId ?? 0;
        this.Template = loaded.Record?.Template ?? string.Empty;
        this.WikiUrl = loaded.Record?.WikiUrl ?? string.Empty;
        this.PageTitle = loaded.Record?.PageTitle;
    }
}
