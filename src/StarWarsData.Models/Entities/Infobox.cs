using System.Web;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Represents a standalone infobox record for storage and processing.
/// Includes page-level properties like PageId, WikiUrl, etc. since this is used as an independent entity.
/// For nested infobox objects within a Page, use PageInfobox instead.
/// </summary>
public class Infobox
{
    [BsonId]
    [BsonElement]    
    public int PageId { get; set; }

    [BsonElement]
    public string? WikiUrl { get; set; }    
    
    [BsonElement]
    public string Template { get; set; } = null!;

    [BsonElement]
    public string? TemplateUrl { get; set; }

    [BsonElement]
    public string PageTitle { get; set; } = null!;

    [BsonElement]
    public string? ImageUrl { get; set; }

    [BsonElement]
    public List<InfoboxProperty> Data { get; set; } = [];

    [BsonElement]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("downloadedAt")]
    public DateTime DownloadedAt { get; set; }
}
