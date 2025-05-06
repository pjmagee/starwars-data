using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Queries;

namespace StarWarsData.Models.Mongo;

public class TimelineEvent
{
    [BsonId] // Assuming you want MongoDB to generate IDs
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; } // Made nullable

    [BsonElement("Title")]
    public string? Title { get; set; }

    [BsonElement("Template")]
    public string? Template { get; set; }

    [BsonElement("CleanedTemplate")]
    public string? CleanedTemplate { get; set; } // Cleaned template name (e.g., Character)

    [BsonElement("ImageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("Demarcation")]
    [BsonRepresentation(BsonType.String)]
    public Demarcation Demarcation { get; set; }

    [BsonElement("Year")]
    public float? Year { get; set; }

    [BsonElement("Properties")]
    public List<InfoboxProperty> Properties { get; set; } = new(); // Initialize to avoid nulls

    [BsonElement("DateEvent")]
    public string? DateEvent { get; set; }
}

