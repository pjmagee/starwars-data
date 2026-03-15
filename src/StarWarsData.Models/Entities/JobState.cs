using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class JobState
{
    [BsonId]
    public required string JobName { get; set; }

    [BsonElement("continueToken")]
    public string? ContinueToken { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
