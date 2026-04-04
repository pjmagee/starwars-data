using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class HyperLink
{
    [BsonElement]
    public string Content { get; set; } = null!;

    [BsonElement]
    public string Href { get; set; } = null!;
}

public class InfoboxProperty
{
    [BsonElement]
    public string? Label { get; set; }

    [BsonElement]
    public List<HyperLink> Links { get; set; }

    [BsonElement]
    public List<string> Values { get; set; }

    public InfoboxProperty(string label, DataValue dataValue)
    {
        Label = label;
        Links = dataValue.Links;
        Values = dataValue.Values;
    }

    public InfoboxProperty()
    {
        Label = null;
        Links = [];
        Values = [];
    }
}

public class DataValue
{
    [BsonElement]
    public List<string> Values { get; set; } = null!;

    [BsonElement]
    public List<HyperLink> Links { get; set; } = null!;
}
