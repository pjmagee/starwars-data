using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eArchetype : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("className")]
    public string ClassName { get; set; } = "";

    [BsonElement("text")]
    public string? Text { get; set; }

    [BsonElement("text2")]
    public string? Text2 { get; set; }

    [BsonElement("casterRatio")]
    public int CasterRatio { get; set; }

    [BsonElement("casterType")]
    public string CasterType { get; set; } = "None";

    [BsonElement("classCasterType")]
    public string ClassCasterType { get; set; } = "None";

    [BsonElement("imageUrls")]
    public List<string> ImageUrls { get; set; } = [];
}
