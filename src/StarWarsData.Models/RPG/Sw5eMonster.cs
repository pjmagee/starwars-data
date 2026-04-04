using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eMonster : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("flavorText")]
    public string? FlavorText { get; set; }

    [BsonElement("sectionText")]
    public string? SectionText { get; set; }

    [BsonElement("size")]
    public string Size { get; set; } = "";

    [BsonElement("types")]
    public List<string> Types { get; set; } = [];

    [BsonElement("alignment")]
    public string Alignment { get; set; } = "";

    [BsonElement("armorClass")]
    public int ArmorClass { get; set; }

    [BsonElement("armorType")]
    public string? ArmorType { get; set; }

    [BsonElement("hitPoints")]
    public int HitPoints { get; set; }

    [BsonElement("hitPointRoll")]
    public string? HitPointRoll { get; set; }

    [BsonElement("speed")]
    public int Speed { get; set; }

    [BsonElement("speeds")]
    public string? Speeds { get; set; }

    [BsonElement("strength")]
    public int Strength { get; set; }

    [BsonElement("dexterity")]
    public int Dexterity { get; set; }

    [BsonElement("constitution")]
    public int Constitution { get; set; }

    [BsonElement("intelligence")]
    public int Intelligence { get; set; }

    [BsonElement("wisdom")]
    public int Wisdom { get; set; }

    [BsonElement("charisma")]
    public int Charisma { get; set; }

    [BsonElement("savingThrows")]
    public string? SavingThrows { get; set; }

    [BsonElement("skills")]
    public List<string> Skills { get; set; } = [];

    [BsonElement("damageImmunities")]
    public List<string> DamageImmunities { get; set; } = [];

    [BsonElement("damageResistances")]
    public List<string> DamageResistances { get; set; } = [];

    [BsonElement("damageVulnerabilities")]
    public List<string> DamageVulnerabilities { get; set; } = [];

    [BsonElement("conditionImmunities")]
    public List<string> ConditionImmunities { get; set; } = [];

    [BsonElement("senses")]
    public List<string> Senses { get; set; } = [];

    [BsonElement("languages")]
    public List<string> Languages { get; set; } = [];

    [BsonElement("challengeRating")]
    public string ChallengeRating { get; set; } = "0";

    [BsonElement("experiencePoints")]
    public int ExperiencePoints { get; set; }

    [BsonElement("behaviors")]
    public List<Sw5eMonsterBehavior> Behaviors { get; set; } = [];

    [BsonElement("imageUrls")]
    public List<string> ImageUrls { get; set; } = [];
}

public class Sw5eMonsterBehavior
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("monsterBehaviorType")]
    public string MonsterBehaviorType { get; set; } = "";

    [BsonElement("description")]
    public string Description { get; set; } = "";

    [BsonElement("attackType")]
    public string AttackType { get; set; } = "None";

    [BsonElement("restrictions")]
    public string? Restrictions { get; set; }

    [BsonElement("attackBonus")]
    public int AttackBonus { get; set; }

    [BsonElement("range")]
    public string? Range { get; set; }

    [BsonElement("numberOfTargets")]
    public string? NumberOfTargets { get; set; }

    [BsonElement("damage")]
    public string? Damage { get; set; }

    [BsonElement("damageRoll")]
    public string? DamageRoll { get; set; }

    [BsonElement("damageType")]
    public string DamageType { get; set; } = "Unknown";
}
