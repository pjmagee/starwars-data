using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

public class HyperLink
{
    [BsonElement, JsonInclude]
    public string Content { get; set; } = null!;
    
    [BsonElement, JsonInclude]
    public string Href { get; set; } = null!;
}

public class InfoboxProperty
{
    [JsonInclude, BsonElement]
    public string? Label { get; set; }

    [JsonInclude, BsonElement]
    public List<HyperLink> Links { get; set; }

    [JsonInclude, BsonElement]
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
        Links = new List<HyperLink>();
        Values = new List<string>();
    }
}
    
public class DataValue
{
    [BsonElement]
    public List<string> Values { get; set; } = null!;

    [BsonElement]
    public List<HyperLink> Links { get; set; } = null!;
}