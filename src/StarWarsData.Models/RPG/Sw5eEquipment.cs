using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.RPG;

public class Sw5eEquipment : Sw5eBase
{
    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("cost")]
    public int Cost { get; set; }

    [BsonElement("weight")]
    public string? Weight { get; set; }

    [BsonElement("equipmentCategory")]
    public string EquipmentCategory { get; set; } = "";

    [BsonElement("damageNumberOfDice")]
    public int DamageNumberOfDice { get; set; }

    [BsonElement("damageType")]
    public string? DamageType { get; set; }

    [BsonElement("damageDieModifier")]
    public int DamageDieModifier { get; set; }

    [BsonElement("damageDieType")]
    public int DamageDieType { get; set; }

    [BsonElement("weaponClassification")]
    public string? WeaponClassification { get; set; }

    [BsonElement("armorClassification")]
    public string? ArmorClassification { get; set; }

    [BsonElement("properties")]
    public List<string>? Properties { get; set; }

    [BsonElement("ac")]
    public string? AC { get; set; }

    [BsonElement("strengthRequirement")]
    public string? StrengthRequirement { get; set; }

    [BsonElement("stealthDisadvantage")]
    public bool StealthDisadvantage { get; set; }
}
