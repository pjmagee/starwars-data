namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical BSON field names for the <see cref="TimelineEvent"/> document in the
/// <c>timeline.*</c> collections. These match the values on the <c>[BsonElement("…")]</c>
/// attributes. Note that unlike <see cref="Page"/> and <see cref="GraphNode"/>, timeline
/// events use PascalCase BSON field names rather than camelCase.
/// </summary>
public static class TimelineEventBsonFields
{
    public const string Title = "Title";
    public const string TemplateUri = "TemplateUri";
    public const string Template = "Template";
    public const string ImageUrl = "ImageUrl";
    public const string Demarcation = "Demarcation";
    public const string Year = "Year";
    public const string Calendar = "Calendar";
    public const string RealYear = "RealYear";
    public const string Properties = "Properties";
    public const string DateEvent = "DateEvent";
    public const string Continuity = "Continuity";
    public const string Realm = "Realm";
    public const string PageId = "PageId";
    public const string WikiUrl = "WikiUrl";
}
