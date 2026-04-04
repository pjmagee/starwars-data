using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eClass : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("summary")]
    public string Summary { get; set; } = "";

    [BsonElement("primaryAbility")]
    public string PrimaryAbility { get; set; } = "";

    [BsonElement("flavorText")]
    public string? FlavorText { get; set; }

    [BsonElement("creatingText")]
    public string? CreatingText { get; set; }

    [BsonElement("quickBuildText")]
    public string? QuickBuildText { get; set; }

    [BsonElement("hitDiceDieType")]
    public int HitDiceDieType { get; set; }

    [BsonElement("hitPointsAtFirstLevel")]
    public string HitPointsAtFirstLevel { get; set; } = "";

    [BsonElement("hitPointsAtHigherLevels")]
    public string HitPointsAtHigherLevels { get; set; } = "";

    [BsonElement("hitPointsAtFirstLevelNumber")]
    public int HitPointsAtFirstLevelNumber { get; set; }

    [BsonElement("hitPointsAtHigherLevelsNumber")]
    public int HitPointsAtHigherLevelsNumber { get; set; }

    [BsonElement("armorProficiencies")]
    public List<string> ArmorProficiencies { get; set; } = [];

    [BsonElement("weaponProficiencies")]
    public List<string> WeaponProficiencies { get; set; } = [];

    [BsonElement("toolProficiencies")]
    public List<string> ToolProficiencies { get; set; } = [];

    [BsonElement("savingThrows")]
    public List<string> SavingThrows { get; set; } = [];

    [BsonElement("skillChoices")]
    public string? SkillChoices { get; set; }

    [BsonElement("numSkillChoices")]
    public int NumSkillChoices { get; set; }

    [BsonElement("skillChoicesList")]
    public List<string> SkillChoicesList { get; set; } = [];

    [BsonElement("equipmentLines")]
    public List<string> EquipmentLines { get; set; } = [];

    [BsonElement("startingWealthVariant")]
    public string? StartingWealthVariant { get; set; }

    [BsonElement("classFeatureText")]
    public string? ClassFeatureText { get; set; }

    [BsonElement("archetypeFlavorText")]
    public string? ArchetypeFlavorText { get; set; }

    [BsonElement("archetypeFlavorName")]
    public string? ArchetypeFlavorName { get; set; }

    [BsonElement("casterRatio")]
    public int CasterRatio { get; set; }

    [BsonElement("casterType")]
    public string CasterType { get; set; } = "None";

    [BsonElement("multiClassProficiencies")]
    public List<string> MultiClassProficiencies { get; set; } = [];

    [BsonElement("imageUrls")]
    public List<string> ImageUrls { get; set; } = [];
}
