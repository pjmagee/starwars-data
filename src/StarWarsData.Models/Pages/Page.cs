using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class Page
{
    [BsonId]
    [BsonElement("pageId")]
    public required int PageId { get; set; }

    [BsonElement("title")]
    public required string Title { get; set; }

    [BsonElement("infobox")]
    public PageInfobox? Infobox { get; set; }

    /// <summary>
    /// Raw infobox JSON string from the Fandom pageprops API (ppprop=infoboxes).
    /// Stored so the infobox can be re-parsed locally without re-downloading from the wiki.
    /// </summary>
    [BsonElement("rawInfobox")]
    [BsonIgnoreIfNull]
    public string? RawInfobox { get; set; }

    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    [BsonElement("categories")]
    public required List<string> Categories { get; set; }

    [BsonElement("images")]
    public required List<MediaInfo> Images { get; set; }

    [BsonElement("wikiUrl")]
    public required string WikiUrl { get; set; }

    [BsonElement("lastModified")]
    public DateTime LastModified { get; set; }

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("universe")]
    [BsonRepresentation(BsonType.String)]
    public Universe Universe { get; set; } = Universe.Unknown;

    [BsonElement("downloadedAt")]
    public DateTime DownloadedAt { get; set; }

    /// <summary>
    /// SHA-256 hash of Content + serialised Infobox. Used to detect meaningful
    /// content changes during incremental sync and trigger downstream re-processing.
    /// </summary>
    [BsonElement("contentHash")]
    public string? ContentHash { get; set; }
}
