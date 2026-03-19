using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class ChatSession
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; }

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [BsonElement("messages")]
    public List<ChatSessionMessage> Messages { get; set; } = [];
}

public class ChatSessionMessage
{
    [BsonElement("role")]
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [BsonElement("content")]
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [BsonElement("toolName")]
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [BsonElement("timestamp")]
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}
