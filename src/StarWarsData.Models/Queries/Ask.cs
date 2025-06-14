using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StarWarsData.Models.Queries;

public sealed record UserPrompt(string Question);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AskChartType
{
    Bar,
    Donut,
    Line,
    Pie,
    StackedBar,
    TimeSeries,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSeriesDisplayType
{
    Line,
    Area,
}

public class AskResponse
{
    [JsonPropertyName("chart")]
    public AskChart? Chart { get; set; }
}

[Description("A chart to render")]
public class AskChart
{
    [JsonPropertyName("chartType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("The type of chart to render")]
    public AskChartType AskChartType { get; set; }

    [JsonPropertyName("timeSeriesDisplayType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description(
        "The type of time series chart to render (line or area) and only used when ChartType == TimeSeries"
    )]
    public TimeSeriesDisplayType? TimeSeriesDisplayType { get; set; }

    [JsonPropertyName("title")]
    [Description("The title of the chart")]
    public string? Title { get; set; }

    [JsonPropertyName("xAxisLabels")]
    [Description("Category labels for X-axis (bar, line, stacked)")]
    public List<string>? XAxisLabels { get; set; }

    [JsonPropertyName("labels")]
    [Description("Slice labels for pie/donut charts")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("series")]
    [Description("Numeric series for bar, line, stacked")]
    public List<AskChartSeries>? Series { get; set; }

    [JsonPropertyName("timeSeries")]
    [Description("Time-series data: date + value pairs, per series")]
    public List<AskTimeSeriesChartSeries>? TimeSeries { get; set; }

    [JsonPropertyName("options")]
    public AskChartOptions? Options { get; set; }
}

[Description("A series of data for a chart")]
public class AskChartSeries
{
    [JsonPropertyName("name")]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Description("The data points for the series")]
    public List<double> Data { get; set; } = [];
}

[Description("A series of data for a time series chart")]
public class AskTimeSeriesChartSeries
{
    [JsonPropertyName("name")]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Description("The data points for the series")]
    public List<TimeSeriesDataPointDto> Data { get; set; } = [];
}

[Description("A data point for a time series chart")]
public class TimeSeriesDataPointDto
{
    [JsonPropertyName("x")]
    [Description("The date of the data point")]
    public DateTime X { get; set; }

    [JsonPropertyName("y")]
    [Description("The value of the data point")]
    public double Y { get; set; }
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
