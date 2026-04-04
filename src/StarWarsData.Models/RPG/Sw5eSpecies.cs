using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eSpecies : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("skinColorOptions")]
    public string? SkinColorOptions { get; set; }

    [BsonElement("hairColorOptions")]
    public string? HairColorOptions { get; set; }

    [BsonElement("eyeColorOptions")]
    public string? EyeColorOptions { get; set; }

    [BsonElement("distinctions")]
    public string? Distinctions { get; set; }

    [BsonElement("heightAverage")]
    public string? HeightAverage { get; set; }

    [BsonElement("heightRollMod")]
    public string? HeightRollMod { get; set; }

    [BsonElement("weightAverage")]
    public string? WeightAverage { get; set; }

    [BsonElement("weightRollMod")]
    public string? WeightRollMod { get; set; }

    [BsonElement("homeworld")]
    public string? Homeworld { get; set; }

    [BsonElement("flavorText")]
    public string? FlavorText { get; set; }

    [BsonElement("language")]
    public string? Language { get; set; }

    [BsonElement("size")]
    public string? Size { get; set; }

    [BsonElement("traits")]
    public List<Sw5eTrait> Traits { get; set; } = [];

    [BsonElement("abilitiesIncreased")]
    public List<List<Sw5eAbilityIncrease>> AbilitiesIncreased { get; set; } = [];

    [BsonElement("imageUrls")]
    public List<string> ImageUrls { get; set; } = [];
}

public class Sw5eTrait
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("description")]
    public string Description { get; set; } = "";
}

public class Sw5eAbilityIncrease
{
    [BsonElement("abilities")]
    public List<string> Abilities { get; set; } = [];

    [BsonElement("amount")]
    public int Amount { get; set; }
}
