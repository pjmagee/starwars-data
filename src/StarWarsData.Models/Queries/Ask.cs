using System.ComponentModel;
using System.Text.Json.Serialization;

namespace StarWarsData.Models.Queries;

public sealed record UserPrompt(string Question, string? Continuity = null);

// ── Chart subtypes (Bar, Line, Pie, etc.) ──────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AskChartType
{
    [Description(
        "Bar chart — counts or comparisons across named categories. Requires xAxisLabels and series."
    )]
    Bar,

    [Description(
        "Donut chart — proportions of a whole. Requires labels and series where each series.data has one value per label."
    )]
    Donut,

    [Description("Line chart — trends over an ordinal axis. Requires xAxisLabels and series.")]
    Line,

    [Description(
        "Pie chart — proportions of a whole. Requires labels and series where each series.data has one value per label."
    )]
    Pie,

    [Description(
        "Stacked bar chart — multiple numeric series across the same categories. Requires xAxisLabels and series."
    )]
    StackedBar,

    [Description("Time series chart — data points with real ISO dates. Requires timeSeries.")]
    TimeSeries,

    [Description(
        "Radar chart — data displayed on multiple axes from a central point. Requires xAxisLabels (axis names) and series."
    )]
    Radar,

    [Description(
        "Rose chart — proportions of a whole displayed as a polar area chart. Requires labels and series where each series.data has one value per label."
    )]
    Rose,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSeriesDisplayType
{
    Line,
    Area,
}

// ── Component Descriptors ──────────────────────────────────────────────

[Description("Table component configuration — the frontend fetches paginated data from the API")]
public class TableDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the table")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("collection")]
    [Description("The MongoDB collection to query (e.g. Character, Battle, Planet, ForcePower)")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    [Description(
        "Which Data.Label fields to show as columns (e.g. [\"Born\", \"Died\", \"Homeworld\", \"Species\"]). Always include 3-6 relevant fields."
    )]
    public List<string> Fields { get; set; } = [];

    [JsonPropertyName("search")]
    [Description("Optional text search to filter results")]
    public string? Search { get; set; }

    [JsonPropertyName("pageSize")]
    [Description("Number of rows per page (default 25)")]
    public int PageSize { get; set; } = 25;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description(
    "Ad-hoc data table — the AI provides the actual row data inline, for custom aggregations or sampled results"
)]
public class DataTableDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the table")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    [Description("Column headers for the table")]
    public List<string> Columns { get; set; } = [];

    [JsonPropertyName("rows")]
    [Description("Row data — each row is a list of string values matching the columns order")]
    public List<List<string>> Rows { get; set; } = [];

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description(
    "Chart component configuration — data is embedded since aggregations are unique per query"
)]
public class ChartDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the chart")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("chartType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("The type of chart to render")]
    public AskChartType ChartType { get; set; }

    [JsonPropertyName("timeSeriesDisplayType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("Line or Area display, only used when chartType == TimeSeries")]
    public TimeSeriesDisplayType? TimeSeriesDisplayType { get; set; }

    [JsonPropertyName("xAxisLabels")]
    [Description("Category labels for X-axis (bar, line, stacked)")]
    public List<string>? XAxisLabels { get; set; }

    [JsonPropertyName("labels")]
    [Description("Slice labels for pie/donut charts")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("series")]
    [Description(
        "Numeric series. For Bar/Line/StackedBar: one data value per xAxisLabels entry. For Pie/Donut: one series named 'Values' with one data value per labels entry."
    )]
    public List<ChartSeries>? Series { get; set; }

    [JsonPropertyName("timeSeries")]
    [Description("Time-series data: date + value pairs, per series")]
    public List<TimeSeriesChartSeries>? TimeSeries { get; set; }

    [JsonPropertyName("options")]
    public ChartOptions? Options { get; set; }

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description(
    "Relationship graph powered by the knowledge graph (kg.edges). "
        + "The frontend fetches connected entities via BFS traversal of kg.edges and renders a D3 network or tree. "
        + "Call get_relationship_types(entityId) first to discover available edge labels."
)]
public class GraphDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the graph")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("rootEntityId")]
    [Description("The entity's PageId from the knowledge graph")]
    public int RootEntityId { get; set; }

    [JsonPropertyName("rootEntityName")]
    [Description("The entity's display name")]
    public string RootEntityName { get; set; } = string.Empty;

    [JsonPropertyName("maxDepth")]
    [Description(
        "How many hops to traverse (default 2). Use 1 for direct relationships, 2-3 for multi-hop exploration."
    )]
    public int MaxDepth { get; set; } = 2;

    [JsonPropertyName("labels")]
    [Description(
        "KG edge labels to traverse (e.g. child_of, parent_of, head_of_state, affiliated_with). "
            + "Call get_relationship_types(entityId) to discover available labels. "
            + "Pass only labels relevant to the question to focus the graph."
    )]
    public List<string> Labels { get; set; } = [];

    [JsonPropertyName("enabledLabels")]
    [Description(
        "Labels to show by default. Subset of labels. If omitted, all labels are enabled."
    )]
    public List<string>? EnabledLabels { get; set; }

    [JsonPropertyName("layoutMode")]
    [Description(
        "Layout mode: 'force' for physics-based network graph (default), 'tree' for hierarchical layout. "
            + "Use 'tree' for family trees, lineages, or organizational hierarchies."
    )]
    public string LayoutMode { get; set; } = "force";

    [JsonPropertyName("continuity")]
    [Description("Optional continuity filter: Canon, Legends, or omit for all")]
    public string? Continuity { get; set; }

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description(
    "Timeline component configuration — the frontend fetches paginated timeline events from the API"
)]
public class TimelineDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the timeline")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    [Description(
        "Timeline event categories to include (e.g. [\"Battle_infobox\", \"War_infobox\", \"Character_infobox\"]). Use available-categories to discover valid names."
    )]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("pageSize")]
    [Description("Number of year groups per page (default 15)")]
    public int PageSize { get; set; } = 15;

    [JsonPropertyName("yearFrom")]
    [Description("Start of year range filter (e.g. 41 for 41 BBY)")]
    public float? YearFrom { get; set; }

    [JsonPropertyName("yearFromDemarcation")]
    [Description("BBY or ABY for the start year")]
    public string? YearFromDemarcation { get; set; }

    [JsonPropertyName("yearTo")]
    [Description("End of year range filter (e.g. 4 for 4 ABY)")]
    public float? YearTo { get; set; }

    [JsonPropertyName("yearToDemarcation")]
    [Description("BBY or ABY for the end year")]
    public string? YearToDemarcation { get; set; }

    [JsonPropertyName("search")]
    [Description("Optional text to filter timeline event titles")]
    public string? Search { get; set; }

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description(
    "Infobox card — renders a wiki-style infobox for one or more pages. The frontend fetches full Page data by ID."
)]
public class InfoboxDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title (e.g. 'Mace Windu' or 'Comparing Yoda and Dooku')")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("pageIds")]
    [Description("One or more PageId integers to display as infobox cards")]
    public List<int> PageIds { get; set; } = [];

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Text content — renders article text, summaries, or RAG excerpts from wiki pages")]
public class TextDescriptor
{
    [JsonPropertyName("title")]
    [Description("Descriptive title for the text section")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    [Description("Text sections to display, each with a heading and content")]
    public List<TextSection> Sections { get; set; } = [];

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

public class TextSection
{
    [JsonPropertyName("heading")]
    [Description("Section heading (e.g. 'Biography', 'Powers and Abilities')")]
    public string Heading { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [Description("The text content — plain text or markdown")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sourcePageId")]
    [Description("Optional PageId this text was sourced from")]
    public int? SourcePageId { get; set; }

    [JsonPropertyName("sourcePageTitle")]
    [Description("Optional title of the source page")]
    public string? SourcePageTitle { get; set; }
}

// ── References ────────────────────────────────────────────────────────

[Description("A source reference link from a wiki page")]
public class Reference
{
    [JsonPropertyName("title")]
    [Description("Display title of the source page")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    [Description("The Wookieepedia URL for the source page")]
    public string Url { get; set; } = string.Empty;
}

// ── Chart data types ───────────────────────────────────────────────────

[Description("A named series of numeric data")]
public class ChartSeries
{
    [JsonPropertyName("name")]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Description("The data points for the series")]
    public List<double> Data { get; set; } = [];
}

[Description("A named series of time-series data")]
public class TimeSeriesChartSeries
{
    [JsonPropertyName("name")]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Description("The data points for the series")]
    public List<TimeSeriesDataPoint> Data { get; set; } = [];
}

[Description("A data point for a time series chart")]
public class TimeSeriesDataPoint
{
    [JsonPropertyName("x")]
    [Description("The date of the data point")]
    public DateTime X { get; set; }

    [JsonPropertyName("y")]
    [Description("The value of the data point")]
    public double Y { get; set; }
}

public class ChartOptions
{
    [JsonPropertyName("chartPalette")]
    public List<string>? ChartPalette { get; set; }

    [JsonPropertyName("stacked")]
    public bool? Stacked { get; set; }
}
