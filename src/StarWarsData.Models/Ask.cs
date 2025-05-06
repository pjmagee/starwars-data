using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StarWarsData.Models;

public sealed record UserPrompt(string Question);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChartKind
{
    Bar,
    Pie,
    Donut,
    Line,
    Stacked,
    TimeSeries
}

public enum AggOp
{
    Count,
    Sum,
    Avg,
    Min,
    Max
}

public class RowDto
{
    public string Label { get; set; }
    public double Value { get; set; }

    public RowDto(string label, double value)
    {
        Label = label;
        Value = value;

    }
}

[Description("The chart data")]
public class ChartSpec
{
    [JsonPropertyName("kind")]
    [Description("The kind of the chart")]
    public ChartKind Kind { get; set; }
    
    [JsonPropertyName("labels")]
    [Description("The labels of the chart")]
    public string[] Labels { get; set; }
    
    [JsonPropertyName("series")]
    [Description("The series of the chart")]
    public double[] Series { get; set; }
    
    [JsonPropertyName("title")]
    [Description("The title of the chart")]
    public string? Title { get; set; }
    
    [JsonPropertyName("legend")]
    
    public string? Legend { get; set; }
}
