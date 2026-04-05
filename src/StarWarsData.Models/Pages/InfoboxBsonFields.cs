namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="Infobox"/>, <see cref="InfoboxProperty"/>,
/// and <see cref="HyperLink"/> models. These match the field names produced by the default
/// <c>[BsonElement]</c> serialization of each property and are used from raw BSON access
/// code (<see cref="MongoDB.Bson.BsonDocument"/> indexers, <c>$elemMatch</c> filters, etc.).
/// </summary>
/// <remarks>
/// Prefer these constants over hardcoded strings so a property rename on the model is a
/// single-point change. The rename would still require a MongoDB data migration — these
/// constants simply make the name explicit and grep-able.
/// </remarks>
public static class InfoboxBsonFields
{
    // ── Infobox top-level ──
    public const string Template = "Template";
    public const string ImageUrl = "ImageUrl";
    public const string Data = "Data";

    // ── InfoboxProperty (entries inside Data) ──
    public const string Label = "Label";
    public const string Values = "Values";
    public const string Links = "Links";

    /// <summary>Dotted BSON path to the first element of the Values array. Used in $exists filters.</summary>
    public const string ValuesFirst = Values + ".0";

    // ── HyperLink (entries inside Links) ──
    public const string Content = "Content";
    public const string Href = "Href";
}
