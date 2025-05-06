using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StarWarsData.Models;

public sealed record UserPrompt(string Question);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AskChartType
{
    Bar,
    Donut,
    Line,
    Pie,
    StackedBar,
    TimeSeries
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSeriesDisplayType
{
    Line,
    Area
}

[Description("A chart to render")]
public class AskChart
{
    [JsonPropertyName("chartType")] 
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("The type of chart to render")]
    public AskChartType AskChartType { get; set; }

    /// <summary>
    /// Only used when ChartType == TimeSeries
    /// </summary>
    [JsonPropertyName("timeSeriesDisplayType")]
    public TimeSeriesDisplayType? TimeSeriesDisplayType { get; set; }

    [JsonPropertyName("title")] 
    [Description("The title of the chart")]
    public string? Title { get; set; }

    /// <summary>
    /// Category labels for X-axis (bar, line, stacked)
    /// </summary>
    [JsonPropertyName("xAxisLabels")]
    public List<string>? XAxisLabels { get; set; }

    /// <summary>
    /// Slice labels for pie/donut charts
    /// </summary>
    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    /// <summary>
    /// Numeric series for bar/line/stacked/scatter/bubble
    /// </summary>
    [JsonPropertyName("series")]
    public List<AskChartSeries>? Series { get; set; }

    /// <summary>
    /// Time-series data: date + value pairs, per series
    /// </summary>
    [JsonPropertyName("timeSeries")]
    public List<AskTimeSeriesChartSeries>? TimeSeries { get; set; }

    [JsonPropertyName("options")] public AskChartOptions? Options { get; set; }
}

public class AskChartSeries
{
    [JsonPropertyName("name")] public string Name { get; set; } = default!;

    [JsonPropertyName("data")] public List<double> Data { get; set; } = new();
}

public class AskTimeSeriesChartSeries
{
    [JsonPropertyName("name")] public string Name { get; set; } = default!;

    [JsonPropertyName("data")] public List<TimeSeriesDataPointDto> Data { get; set; } = new();
}

public class TimeSeriesDataPointDto
{
    [JsonPropertyName("x")] public DateTime X { get; set; }

    [JsonPropertyName("y")] public double Y { get; set; }
}

public class AskChartOptions
{
    /// <summary>
    /// e.g. ["#FF6384","#36A2EB",…] or Material colors
    /// </summary>
    [JsonPropertyName("chartPalette")]
    public List<string>? ChartPalette { get; set; }

    /// <summary>
    /// for stacking bar/area charts
    /// </summary>
    [JsonPropertyName("stacked")]
    public bool? Stacked { get; set; }
}