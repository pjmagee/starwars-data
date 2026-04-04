using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

/// <summary>
/// Feature gained from a class, archetype, or species at a specific level.
/// </summary>
public class Sw5eFeature : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("text")]
    public string Text { get; set; } = "";

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("source")]
    public string Source { get; set; } = "";

    [BsonElement("sourceName")]
    public string SourceName { get; set; } = "";

    [BsonElement("metadata")]
    public string? Metadata { get; set; }
}

/// <summary>
/// Feat: optional ability chosen at ASI levels.
/// </summary>
public class Sw5eFeat : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("prerequisite")]
    public string? Prerequisite { get; set; }

    [BsonElement("text")]
    public string Text { get; set; } = "";

    [BsonElement("attributesIncreased")]
    public List<string> AttributesIncreased { get; set; } = [];
}

/// <summary>
/// Status condition (Blinded, Stunned, Prone, etc.).
/// </summary>
public class Sw5eCondition : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// Skill with its base ability score.
/// </summary>
public class Sw5eSkill : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("baseAttribute")]
    public string BaseAttribute { get; set; } = "";
}

/// <summary>
/// XP thresholds and proficiency bonus per level (1-20).
/// </summary>
public class Sw5eCharacterAdvancement : Sw5eBase
{
    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("experiencePoints")]
    public int ExperiencePoints { get; set; }

    [BsonElement("proficiencyBonus")]
    public int ProficiencyBonus { get; set; }
}

/// <summary>
/// Combat maneuver.
/// </summary>
public class Sw5eManeuver : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("description")]
    public string Description { get; set; } = "";

    [BsonElement("prerequisite")]
    public string? Prerequisite { get; set; }

    [BsonElement("type")]
    public string? Type { get; set; }

    [BsonElement("metadata")]
    public string? Metadata { get; set; }
}

/// <summary>
/// Enhanced/magical item with rarity.
/// </summary>
public class Sw5eEnhancedItem : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("type")]
    public string Type { get; set; } = "";

    [BsonElement("rarityText")]
    public string RarityText { get; set; } = "";

    [BsonElement("searchableRarity")]
    public string? SearchableRarity { get; set; }

    [BsonElement("requiresAttunement")]
    public bool RequiresAttunement { get; set; }

    [BsonElement("text")]
    public string Text { get; set; } = "";

    [BsonElement("hasPrerequisite")]
    public bool HasPrerequisite { get; set; }

    [BsonElement("prerequisite")]
    public string? Prerequisite { get; set; }

    [BsonElement("subtype")]
    public string? Subtype { get; set; }
}

/// <summary>
/// Simple name+description/content type used by fighting styles, masteries,
/// lightsaber forms, weapon properties, armor properties, and reference tables.
/// </summary>
public class Sw5eRuleEntry : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Text content — stored as "description", "text", or "content" depending on collection.
    /// BsonElement maps to the most common one; others fall back via BsonExtraElements.
    /// </summary>
    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("text")]
    public string? Text { get; set; }

    [BsonElement("content")]
    public string? Content { get; set; }

    [BsonElement("metadata")]
    public string? Metadata { get; set; }

    /// <summary>Returns whichever text field is populated.</summary>
    public string ResolvedText => Description ?? Text ?? Content ?? "";
}
