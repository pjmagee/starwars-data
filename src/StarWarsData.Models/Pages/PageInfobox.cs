using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Represents infobox data nested within a Page object.
/// Does not include redundant properties like PageId, WikiUrl, etc. since they're on the parent Page.
/// </summary>
public class PageInfobox
{
    [BsonElement]
    public string? Template { get; set; }

    [BsonElement]
    public string? ImageUrl { get; set; }

    [BsonElement]
    public List<InfoboxProperty> Data { get; set; } = [];
}
