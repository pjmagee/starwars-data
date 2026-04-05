namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="Page"/> document in the <c>raw.pages</c>
/// collection. These match the values on the <c>[BsonElement("…")]</c> attributes on each
/// property. Composite dotted paths are provided for common nested access patterns into
/// the embedded <see cref="PageInfobox"/>.
/// </summary>
public static class PageBsonFields
{
    // ── Top-level Page fields ──
    public const string PageId = "pageId";
    public const string Title = "title";
    public const string Infobox = "infobox";
    public const string RawInfobox = "rawInfobox";
    public const string Content = "content";
    public const string Categories = "categories";
    public const string Images = "images";
    public const string WikiUrl = "wikiUrl";
    public const string LastModified = "lastModified";
    public const string Continuity = "continuity";
    public const string Universe = "universe";
    public const string DownloadedAt = "downloadedAt";
    public const string ContentHash = "contentHash";

    // ── Nested paths into the embedded Infobox document ──
    public const string InfoboxTemplate = Infobox + "." + InfoboxBsonFields.Template;
    public const string InfoboxImageUrl = Infobox + "." + InfoboxBsonFields.ImageUrl;
    public const string InfoboxData = Infobox + "." + InfoboxBsonFields.Data;
    public const string InfoboxDataLabel = InfoboxData + "." + InfoboxBsonFields.Label;
    public const string InfoboxDataValues = InfoboxData + "." + InfoboxBsonFields.Values;
    public const string InfoboxDataLinks = InfoboxData + "." + InfoboxBsonFields.Links;
}
