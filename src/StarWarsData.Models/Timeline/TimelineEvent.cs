using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Queries;

namespace StarWarsData.Models.Entities;

public class TimelineEvent : IComparable<TimelineEvent>
{
    [BsonIgnore]
    public string DisplayTitle => $"{DateEvent} {Title}".Trim();

    [BsonIgnore]
    public string DisplayYear =>
        Calendar == Calendar.Real
            ? RealYear.HasValue
                ? FormatRealYear(RealYear.Value)
                : "Unknown Year"
            : Year.HasValue
                ? $"{Year.Value:##,###} {Demarcation}"
                : "Unknown Year";

    static string FormatRealYear(int year) => year < 0 ? $"{-year} BCE" : year.ToString();

    [BsonIgnore]
    public string? DisplayImageWithoutRevision => ImageUrl?.Split(new[] { "/revision" }, StringSplitOptions.RemoveEmptyEntries)[0];

    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("Title")]
    public string? Title { get; set; }

    [BsonElement("TemplateUri")]
    public string? TemplateUri { get; set; }

    [BsonElement("Template")]
    public string? Template { get; set; }

    [BsonElement("ImageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("Demarcation")]
    [BsonRepresentation(representation: BsonType.String)]
    public Demarcation Demarcation { get; set; }

    [BsonElement("Year")]
    public float? Year { get; set; }

    /// <summary>
    /// Calendar system this event is plotted against. Galactic events use
    /// <see cref="Year"/> + <see cref="Demarcation"/>; real events use <see cref="RealYear"/>.
    /// </summary>
    [BsonElement("Calendar")]
    [BsonRepresentation(representation: BsonType.String)]
    public Calendar Calendar { get; set; } = Calendar.Galactic;

    /// <summary>
    /// Signed CE year for real-world events (negative = BCE). Only set when
    /// <see cref="Calendar"/> == <see cref="Calendar.Real"/>.
    /// </summary>
    [BsonElement("RealYear")]
    [BsonIgnoreIfNull]
    public int? RealYear { get; set; }

    [BsonElement("Properties")]
    public List<InfoboxProperty> Properties { get; set; } = []; // Initialize to avoid nulls

    [BsonElement("DateEvent")]
    public string? DateEvent { get; set; }

    [BsonElement("Continuity")]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("Realm")]
    public Realm Realm { get; set; } = Realm.Unknown;

    [BsonElement("PageId")]
    public int? PageId { get; set; }

    [BsonElement("WikiUrl")]
    public string? WikiUrl { get; set; }

    public int CompareTo(TimelineEvent? other)
    {
        if (other is null)
            return 1;

        // Compare linear years across calendars. Real events use signed CE
        // year; galactic events use signed BBY/ABY (BBY negative). Null sorts last.
        var thisLinear = LinearYear();
        var otherLinear = other.LinearYear();

        if (thisLinear is null && otherLinear is null)
            return 0;
        if (thisLinear is null)
            return 1;
        if (otherLinear is null)
            return -1;

        return thisLinear.Value.CompareTo(otherLinear.Value);
    }

    double? LinearYear()
    {
        if (Calendar == Calendar.Real)
            return RealYear.HasValue ? RealYear.Value : null;
        if (!Year.HasValue)
            return null;
        return Demarcation == Demarcation.Bby ? -Year.Value : Year.Value;
    }
}
