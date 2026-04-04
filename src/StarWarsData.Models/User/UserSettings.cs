using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class UserSettings
{
    [BsonId]
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("encryptedOpenAiKey")]
    public string? EncryptedOpenAiKey { get; set; }

    [BsonElement("openAiKeySet")]
    public bool OpenAiKeySet { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
