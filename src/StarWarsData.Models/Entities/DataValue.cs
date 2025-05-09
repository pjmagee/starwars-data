using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class DataValue
{
    [BsonElement]
    public List<string> Values { get; set; } = null!;

    [BsonElement]
    public List<HyperLink> Links { get; set; } = null!;
}