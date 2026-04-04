using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5ePower : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("powerType")]
    public string PowerType { get; set; } = "";

    [BsonElement("prerequisite")]
    public string? Prerequisite { get; set; }

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("castingPeriod")]
    public string CastingPeriod { get; set; } = "";

    [BsonElement("castingPeriodText")]
    public string? CastingPeriodText { get; set; }

    [BsonElement("range")]
    public string Range { get; set; } = "";

    [BsonElement("duration")]
    public string Duration { get; set; } = "";

    [BsonElement("concentration")]
    public bool Concentration { get; set; }

    [BsonElement("forceAlignment")]
    public string ForceAlignment { get; set; } = "None";

    [BsonElement("description")]
    public string Description { get; set; } = "";

    [BsonElement("higherLevelDescription")]
    public string? HigherLevelDescription { get; set; }
}
