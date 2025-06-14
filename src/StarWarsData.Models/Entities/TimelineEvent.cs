using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Queries;

namespace StarWarsData.Models.Entities;

public class TimelineEvent : IComparable<TimelineEvent>
{
    [BsonIgnore]
    public string DisplayTitle => $"{DateEvent} {Title} ({TemplateUri})".Trim();

    [BsonIgnore]
    public string DisplayYear =>
        Year.HasValue ? $"{Year.Value:##,###} {Demarcation}" : "Unknown Year";

    [BsonIgnore]
    public string? DisplayImageWithoutRevision =>
        ImageUrl?.Split(new[] { "/revision" }, StringSplitOptions.RemoveEmptyEntries)[0];

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

    [BsonElement("Properties")]
    public List<InfoboxProperty> Properties { get; set; } = []; // Initialize to avoid nulls

    [BsonElement("DateEvent")]
    public string? DateEvent { get; set; }

    [BsonElement("Continuity")]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    public int CompareTo(TimelineEvent? other)
    {
        if (other is null)
            return 1;

        // Handle null years - sort them after non-null years
        if (!Year.HasValue && !other.Year.HasValue)
            return 0; // Both null, consider equal
        if (!Year.HasValue)
            return 1; // This year is null, sort after other
        if (!other.Year.HasValue)
            return -1; // Other year is null, sort after this

        // Existing demarcation comparison logic
        switch (Demarcation)
        {
            case Demarcation.Bby when other.Demarcation == Demarcation.Aby:
                return -1;
            case Demarcation.Aby when other.Demarcation == Demarcation.Bby:
                return 1;
        }

        // Existing year comparison logic (now using .Value)
        return Demarcation switch
        {
            // BBY years sort descending (e.g., 10 BBY comes before 5 BBY)
            Demarcation.Bby when other.Demarcation == Demarcation.Bby => other.Year.Value.CompareTo(
                Year.Value
            ),
            // ABY years sort ascending (e.g., 5 ABY comes before 10 ABY)
            _ => Year.Value.CompareTo(other.Year.Value),
        };
    }
}
