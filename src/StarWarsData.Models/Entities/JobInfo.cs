using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace StarWarsData.Models.Entities;

public class JobInfo
{
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
    public Guid Id { get; init; }
    
    [BsonElement("name")]
    public string Name { get; init; } = string.Empty;
    
    [BsonElement("status")]
    public JobStatus Status { get; set; }
    
    [BsonElement("error")]
    public string? ErrorMessage { get; set; }
    
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
    
    [BsonElement("startedAt")]
    public DateTime? StartedAt { get; set; }
    
    [BsonElement("completedAt")]
    public DateTime? CompletedAt { get; set; }
    
    [BsonElement("lastUpdatedAt")]
    public DateTime? LastUpdatedAt { get; set; }
}

public enum JobStatus { Pending, Running, Completed, Failed, Cancelled }
