using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eBackground : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("flavorText")]
    public string? FlavorText { get; set; }

    [BsonElement("skillProficiencies")]
    public string? SkillProficiencies { get; set; }

    [BsonElement("toolProficiencies")]
    public string? ToolProficiencies { get; set; }

    [BsonElement("languages")]
    public string? Languages { get; set; }

    [BsonElement("equipment")]
    public string? Equipment { get; set; }

    [BsonElement("suggestedCharacteristics")]
    public string? SuggestedCharacteristics { get; set; }

    [BsonElement("featureName")]
    public string? FeatureName { get; set; }

    [BsonElement("featureText")]
    public string? FeatureText { get; set; }

    [BsonElement("featOptions")]
    public List<Sw5eRollOption> FeatOptions { get; set; } = [];

    [BsonElement("personalityTraitOptions")]
    public List<Sw5eRollOption> PersonalityTraitOptions { get; set; } = [];

    [BsonElement("idealOptions")]
    public List<Sw5eRollOption> IdealOptions { get; set; } = [];

    [BsonElement("flawOptions")]
    public List<Sw5eRollOption> FlawOptions { get; set; } = [];

    [BsonElement("bondOptions")]
    public List<Sw5eRollOption> BondOptions { get; set; } = [];
}

public class Sw5eRollOption
{
    [BsonElement("name")]
    public string? Name { get; set; }

    [BsonElement("roll")]
    public int Roll { get; set; }

    [BsonElement("description")]
    public string? Description { get; set; }
}
