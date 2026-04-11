using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace StarWarsData.Models.Queries;

public sealed record UserPrompt(string Question, string? Continuity = null);

// ── Graph layout modes ────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphLayoutMode
{
    [Description("Physics-based network graph. Nodes repel and edges attract, settling into a natural layout. Best for general exploration and multi-hop networks.")]
    Force,

    [Description("Hierarchical top-down tree layout. Root at top, connections below in rows by BFS depth. Best for family trees, org charts, government hierarchies.")]
    Tree,

    [Description("Horizontal path layout. Nodes arranged left-to-right in chain order with straight-line edges. Used for shortest-path results between two entities.")]
    Path,
}

// ── Chart subtypes (Bar, Line, Pie, etc.) ──────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AskChartType
{
    [Description("Bar chart — counts or comparisons across named categories. Requires xAxisLabels and series.")]
    Bar,

    [Description("Donut chart — proportions of a whole. Requires labels and series where each series.data has one value per label.")]
    Donut,

    [Description("Line chart — trends over an ordinal axis. Requires xAxisLabels and series.")]
    Line,

    [Description("Pie chart — proportions of a whole. Requires labels and series where each series.data has one value per label.")]
    Pie,

    [Description("Stacked bar chart — multiple numeric series across the same categories. Requires xAxisLabels and series.")]
    StackedBar,

    [Description("Time series chart — data points with real ISO dates. Requires timeSeries.")]
    TimeSeries,

    [Description("Radar chart — data displayed on multiple axes from a central point. Requires xAxisLabels (axis names) and series.")]
    Radar,

    [Description("Rose chart — proportions of a whole displayed as a polar area chart. Requires labels and series where each series.data has one value per label.")]
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
    [Required]
    [Description("Descriptive title for the table")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("collection")]
    [Required]
    [Description("The MongoDB collection to query (e.g. Character, Battle, Planet, ForcePower)")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    [Required]
    [MinLength(1)]
    [Description("Which Data.Label fields to show as columns (e.g. [\"Born\", \"Died\", \"Homeworld\", \"Species\"]). Always include 3-6 relevant fields.")]
    public List<string> Fields { get; set; } = [];

    [JsonPropertyName("search")]
    [Description("Optional text search to filter results")]
    public string? Search { get; set; }

    [JsonPropertyName("pageSize")]
    [Description("Number of rows per page (default 25)")]
    public int PageSize { get; set; } = 25;

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the table's key insights. Shown to users on narrow viewports (< 960px) where the wide table cannot be rendered legibly. Always populate this — it is the only thing mobile users will see in place of the table. Use bullet points and bold key entities."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Ad-hoc data table — the AI provides the actual row data inline, for custom aggregations or sampled results")]
public class DataTableDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title for the table")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    [Required]
    [MinLength(1)]
    [Description("Column headers for the table")]
    public List<string> Columns { get; set; } = [];

    [JsonPropertyName("rows")]
    [Required]
    [MinLength(1)]
    [Description("Row data — each row is a list of string values matching the columns order")]
    public List<List<string>> Rows { get; set; } = [];

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the data table's key insights. Shown to users on narrow viewports (< 960px) where the wide table cannot be rendered legibly. Always populate this — it is the only thing mobile users will see in place of the table. Use bullet points and bold key entities."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Chart component configuration — data is embedded since aggregations are unique per query")]
public class ChartDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title for the chart")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("chartType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Required]
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
    [Description("Numeric series. For Bar/Line/StackedBar: one data value per xAxisLabels entry. For Pie/Donut: one series named 'Values' with one data value per labels entry.")]
    public List<ChartSeries>? Series { get; set; }

    [JsonPropertyName("timeSeries")]
    [Description("Time-series data: date + value pairs, per series")]
    public List<TimeSeriesChartSeries>? TimeSeries { get; set; }

    [JsonPropertyName("options")]
    public ChartOptions? Options { get; set; }

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the chart's key insights — top items, distribution, outliers, what the user should take away. Shown to users on narrow viewports (< 960px) where the chart cannot be rendered legibly (axis labels overlap, bars become unreadable). Always populate this — it is the only thing mobile users will see in place of the chart. Use bullet points and include the actual numeric values from the chart."
    )]
    public string MobileSummary { get; set; } = string.Empty;

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
    [Required]
    [Description("Descriptive title for the graph")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("rootEntityId")]
    [Required]
    [Description("The entity's PageId from the knowledge graph")]
    public int RootEntityId { get; set; }

    [JsonPropertyName("rootEntityName")]
    [Required]
    [Description("The entity's display name")]
    public string RootEntityName { get; set; } = string.Empty;

    [JsonPropertyName("maxDepth")]
    [Description("How many hops to traverse (default 2). Use 1 for direct relationships, 2-3 for multi-hop exploration.")]
    public int MaxDepth { get; set; } = 2;

    [JsonPropertyName("labels")]
    [Required]
    [MinLength(1)]
    [Description(
        "KG edge labels to traverse (e.g. child_of, parent_of, head_of_state, affiliated_with). "
            + "Call get_relationship_types(entityId) to discover available labels. "
            + "Pass only labels relevant to the question to focus the graph."
    )]
    public List<string> Labels { get; set; } = [];

    [JsonPropertyName("enabledLabels")]
    [Description("Labels to show by default. Subset of labels. If omitted, all labels are enabled.")]
    public List<string>? EnabledLabels { get; set; }

    [JsonPropertyName("layoutMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("Layout mode: Force (physics-based network, default), Tree (hierarchical top-down), or Path (horizontal chain for shortest-path results).")]
    public GraphLayoutMode LayoutMode { get; set; } = GraphLayoutMode.Force;

    [JsonPropertyName("continuity")]
    [Description("Optional continuity filter: Canon, Legends, or omit for all")]
    public string? Continuity { get; set; }

    [JsonPropertyName("pathData")]
    [Description("Pre-resolved path for focused rendering. When present, the frontend renders only these nodes/edges without a BFS API call.")]
    public PathData? PathData { get; set; }

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the relationship graph's key insights — central entities, important paths, notable connections. Shown to users on narrow viewports (< 960px) where the force-directed graph cannot be navigated by touch. Always populate this — it is the only thing mobile users will see in place of the graph. Use bullet points and bold key entities."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Pre-resolved shortest path between two entities for focused graph rendering.")]
public class PathData
{
    [JsonPropertyName("fromId")]
    [Required]
    public int FromId { get; set; }

    [JsonPropertyName("fromName")]
    [Required]
    public string FromName { get; set; } = string.Empty;

    [JsonPropertyName("toId")]
    [Required]
    public int ToId { get; set; }

    [JsonPropertyName("toName")]
    [Required]
    public string ToName { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    [Required]
    [MinLength(1)]
    public List<PathStep> Steps { get; set; } = [];
}

public class PathStep
{
    [JsonPropertyName("fromId")]
    [Required]
    public int FromId { get; set; }

    [JsonPropertyName("fromName")]
    [Required]
    public string FromName { get; set; } = string.Empty;

    [JsonPropertyName("fromType")]
    [Required]
    public string FromType { get; set; } = string.Empty;

    [JsonPropertyName("toId")]
    [Required]
    public int ToId { get; set; }

    [JsonPropertyName("toName")]
    [Required]
    public string ToName { get; set; } = string.Empty;

    [JsonPropertyName("toType")]
    [Required]
    public string ToType { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    [Required]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = string.Empty;
}

[Description("Timeline component configuration — the frontend fetches paginated timeline events from the API")]
public class TimelineDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title for the timeline")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    [Required]
    [MinLength(1)]
    [Description(
        "Timeline event categories to include (e.g. [\"Battle_infobox\", \"War_infobox\", \"Character_infobox\"] for galactic; [\"Book\", \"Film\", \"Video_game\"] for real-world). Use available-categories to discover valid names."
    )]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("pageSize")]
    [Description("Number of year groups per page (default 15)")]
    public int PageSize { get; set; } = 15;

    [JsonPropertyName("calendar")]
    [Description("Calendar mode: 'Galactic' for in-universe BBY/ABY (default) or 'Real' for real-world CE publication dates.")]
    public string? Calendar { get; set; }

    [JsonPropertyName("yearFrom")]
    [Description("Start of year range filter. Galactic: magnitude (e.g. 41 with yearFromDemarcation='BBY'). Real: signed CE year (e.g. 1977; use negative for BCE).")]
    public float? YearFrom { get; set; }

    [JsonPropertyName("yearFromDemarcation")]
    [Description("BBY or ABY for the start year (galactic mode only; omit for Real calendar).")]
    public string? YearFromDemarcation { get; set; }

    [JsonPropertyName("yearTo")]
    [Description("End of year range filter. Galactic: magnitude (e.g. 4 with yearToDemarcation='ABY'). Real: signed CE year (e.g. 2020).")]
    public float? YearTo { get; set; }

    [JsonPropertyName("yearToDemarcation")]
    [Description("BBY or ABY for the end year (galactic mode only; omit for Real calendar).")]
    public string? YearToDemarcation { get; set; }

    [JsonPropertyName("search")]
    [Description("Optional text to filter timeline event titles")]
    public string? Search { get; set; }

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the timeline's key events with their dates and significance. Shown to users on narrow viewports (< 960px) where the timeline visualization cannot be rendered legibly. Always populate this — it is the only thing mobile users will see in place of the timeline. Use bullet points organized chronologically with dates in **bold**."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Infobox card — renders a wiki-style infobox for one or more pages. The frontend fetches full Page data by ID.")]
public class InfoboxDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title (e.g. 'Mace Windu' or 'Comparing Yoda and Dooku')")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("pageIds")]
    [Required]
    [MinLength(1)]
    [Description("One or more PageId integers to display as infobox cards")]
    public List<int> PageIds { get; set; } = [];

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullet points or short paragraphs) of the infobox subject(s) — key facts, dates, affiliations, comparisons. Shown to users on narrow viewports (< 960px) where the side-by-side cards become illegible. Always populate this — it is the only thing mobile users will see in place of the infobox. Use bullet points and **bold** field names."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

[Description("Text content — renders article text, summaries, or RAG excerpts from wiki pages")]
public class TextDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title for the text section")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    [Required]
    [MinLength(1)]
    [Description("Text sections to display, each with a heading and content")]
    public List<TextSection> Sections { get; set; } = [];

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result")]
    public List<Reference>? References { get; set; }
}

public class TextSection
{
    [JsonPropertyName("heading")]
    [Required]
    [Description("Section heading (e.g. 'Biography', 'Powers and Abilities')")]
    public string Heading { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [Required]
    [Description("The text content — plain text or markdown")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sourcePageId")]
    [Description("Optional PageId this text was sourced from")]
    public int? SourcePageId { get; set; }

    [JsonPropertyName("sourcePageTitle")]
    [Description("Optional title of the source page")]
    public string? SourcePageTitle { get; set; }
}

[Description("English-to-Aurebesh auto-converter. Write plain English — the frontend renders it as Aurebesh.")]
public class AurebeshDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Title shown in normal English font above the Aurebesh output")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [Required]
    [Description("Plain English text (with optional markdown). Automatically displayed as Aurebesh.")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("references")]
    [Description("Optional source references")]
    public List<Reference>? References { get; set; }
}

// ── References ────────────────────────────────────────────────────────

[Description("A source reference link from a wiki page")]
public class Reference
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Display title of the source page")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    [Required]
    [Description("The Wookieepedia URL for the source page")]
    public string Url { get; set; } = string.Empty;
}

// ── Chart data types ───────────────────────────────────────────────────

[Description("A named series of numeric data")]
public class ChartSeries
{
    [JsonPropertyName("name")]
    [Required]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Required]
    [MinLength(1)]
    [Description("The data points for the series")]
    public List<double> Data { get; set; } = [];
}

[Description("A named series of time-series data")]
public class TimeSeriesChartSeries
{
    [JsonPropertyName("name")]
    [Required]
    [Description("The name of the series")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("data")]
    [Required]
    [MinLength(1)]
    [Description("The data points for the series")]
    public List<TimeSeriesDataPoint> Data { get; set; } = [];
}

[Description("A data point for a time series chart")]
public class TimeSeriesDataPoint
{
    [JsonPropertyName("x")]
    [Required]
    [Description("The date of the data point")]
    public DateTime X { get; set; }

    [JsonPropertyName("y")]
    [Required]
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
