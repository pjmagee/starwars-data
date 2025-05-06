using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Mongo;

namespace StarWarsData.Models.Queries;

public class TimelineEvent : IComparable<TimelineEvent>
{
    public string DisplayTitle => $"{DateEvent} {Title} ({Template})".Trim();
    
    // Updated DisplayYear to handle null Year
    public string DisplayYear => Year.HasValue ? $"{Year.Value:##,###} {Demarcation}" : "Unknown Year";
    public string? DisplayImageWithoutRevision => ImageUrl?.Split(new [] {"/revision" }, StringSplitOptions.RemoveEmptyEntries)[0];
    public string? Title { get; set; } // Made nullable
    public string? Template { get; set; } // Made nullable
    public string? ImageUrl { get; set; }
    
    [BsonRepresentation(BsonType.String)] // Add this attribute
    public Demarcation Demarcation { get; set; }
    
    public float? Year { get; set; } // Changed to nullable float
    public List<InfoboxProperty> Properties { get; set; }
    public string? DateEvent { get; set; }
    
    public int CompareTo(TimelineEvent? other)
    {
        if (other is null) return 1;
        
        // Handle null years - sort them after non-null years
        if (!Year.HasValue && !other.Year.HasValue) return 0; // Both null, consider equal
        if (!Year.HasValue) return 1; // This year is null, sort after other
        if (!other.Year.HasValue) return -1; // Other year is null, sort after this
        
        // Existing demarcation comparison logic
        switch (Demarcation)
        {
            case Demarcation.Bby when other.Demarcation == Demarcation.Aby: return -1;
            case Demarcation.Aby when other.Demarcation == Demarcation.Bby: return 1;
        }

        // Existing year comparison logic (now using .Value)
        return Demarcation switch
        {
            // BBY years sort descending (e.g., 10 BBY comes before 5 BBY)
            Demarcation.Bby when other.Demarcation == Demarcation.Bby => other.Year.Value.CompareTo(Year.Value),
            // ABY years sort ascending (e.g., 5 ABY comes before 10 ABY)
            _ => Year.Value.CompareTo(other.Year.Value)
        };
    }
}

