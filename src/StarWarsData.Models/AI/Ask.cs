using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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

// ── Family Tree Descriptor (Design-042) ────────────────────────────────
//
// Wire shape: data-model.md § C# records + contracts/family-tree-endpoint.md.
// The renderer is the vendored family-chart-premium UMD (wwwroot/lib/family-chart-premium/)
// whose JS data contract is { id, data, rels } with literal "first name"/"last name" keys.
// JsonPropertyName attributes preserve that spelling across the wire.

[Description("A single person in a family tree, in the data shape consumed by the family-chart-premium renderer.")]
public class FamilyTreePerson
{
    [JsonPropertyName("id")]
    [Required]
    [Description("Stringified PageId for real entries; \"{pageId}-stub\" for synthetic stubs emitted when a referenced person is missing from the result set.")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [Required]
    public FamilyTreePersonData Data { get; set; } = new();

    [JsonPropertyName("rels")]
    [Required]
    public FamilyTreeRels Rels { get; set; } = new();
}

[Description("Card-visible person fields. The literal 'first name' / 'last name' keys are required by family-chart-premium's JS code.")]
public class FamilyTreePersonData
{
    [JsonPropertyName("gender")]
    [Required]
    [Description(
        "\"M\" or \"F\" — family-chart-premium constraint. Missing/ambiguous infobox gender defaults to \"M\" with the PageId added to limitations.missingGenders. Never propagate outside this descriptor."
    )]
    public string Gender { get; set; } = "M";

    [JsonPropertyName("first name")]
    [Required]
    [Description("First name, derived by splitting the kg.nodes.name on the last whitespace.")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last name")]
    [Required]
    [Description("Last name, derived by splitting the kg.nodes.name on the last whitespace. Empty string when the name is a single token (e.g. \"Yoda\").")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("wikiUrl")]
    [Description("Wookieepedia URL, copied from kg.nodes.wikiUrl.")]
    public string? WikiUrl { get; set; }

    [JsonPropertyName("imageUrl")]
    [Description("Avatar image URL, copied from kg.nodes.imageUrl. May be null.")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("pageId")]
    [Required]
    [Description(
        "The real PageId for both real entries and synthetic stubs (the stub's Id carries a -stub suffix but its PageId is the real one, so the click still navigates to /knowledge-graph/nodes/{pageId})."
    )]
    public int PageId { get; set; }
}

[Description("Family-chart-premium relationship arrays. Bidirectional linking is enforced server-side: every ID listed here must appear in People[].")]
public class FamilyTreeRels
{
    [JsonPropertyName("parents")]
    [Description("Stringified PageIds of parents.")]
    public List<string>? Parents { get; set; }

    [JsonPropertyName("spouses")]
    [Description(
        "Stringified PageIds of spouses — union of partner_of, spouse_of, married_to edges. The premium spouse-link-text plugin re-labels each rendered edge based on the underlying kg.edges label."
    )]
    public List<string>? Spouses { get; set; }

    [JsonPropertyName("children")]
    [Description("Stringified PageIds of children.")]
    public List<string>? Children { get; set; }
}

[Description("A kinship entry surfaced via the premium kinship plugin — covers has_relative edges (cousins, in-laws, etc.) that don't fit family-chart's parents/spouses/children model.")]
public class FamilyTreeKinshipEntry
{
    [JsonPropertyName("personId")]
    [Required]
    [Description("Stringified PageId of the person; must be present in People[].")]
    public string PersonId { get; set; } = string.Empty;

    [JsonPropertyName("relativeId")]
    [Required]
    [Description("Stringified PageId of the relative; must be present in People[].")]
    public string RelativeId { get; set; } = string.Empty;

    [JsonPropertyName("relationship")]
    [Required]
    [Description("Free-text relationship label from the source edge (e.g. \"Cousin\", \"Niece\"). Defaults to \"Relative\" when the edge has no qualifier.")]
    public string Relationship { get; set; } = "Relative";
}

[Description("Metadata block describing soft-handled data shortcuts in the projection. Always present, even when empty.")]
public class FamilyTreeLimitations
{
    [JsonPropertyName("missingGenders")]
    [Required]
    [Description("PageIds whose Gender infobox field was missing/ambiguous and defaulted to \"M\".")]
    public List<int> MissingGenders { get; set; } = [];

    [JsonPropertyName("adoptiveRelationsExcluded")]
    [Required]
    [Description("Relations excluded because they exist only via family-membership edges (no biological parent_of) and v1 doesn't model adoptive parents. Free-text human-readable entries.")]
    public List<string> AdoptiveRelationsExcluded { get; set; } = [];

    [JsonPropertyName("truncatedAtDepth")]
    [Required]
    [Description("True when the BFS hit maxNodes before exhausting maxDepth. Synthetic stubs may appear at the edge of the result set.")]
    public bool TruncatedAtDepth { get; set; }

    [JsonPropertyName("cycleFallback")]
    [Required]
    [Description("True when the renderer crashed or visibly looped on the input and the client should fall back to render_graph Tree mode.")]
    public bool CycleFallback { get; set; }
}

[Description(
    "Endpoint wire shape returned by GET /api/RelationshipGraph/family-tree/{pageId}. Separate from the AI tool's FamilyTreeDescriptor because the endpoint carries only the projection — the descriptor wraps it with title + mobileSummary + references."
)]
public class FamilyTreeResponse
{
    [JsonPropertyName("rootId")]
    [Required]
    [Description("Stringified PageId of the focal Character; always present in People[].")]
    public string RootId { get; set; } = string.Empty;

    [JsonPropertyName("rootName")]
    [Required]
    [Description("kg.nodes.name of the focal Character.")]
    public string RootName { get; set; } = string.Empty;

    [JsonPropertyName("people")]
    [Required]
    [MinLength(1)]
    [Description("All persons in the tree, including the root, ancestors, descendants, spouses, and any synthetic stubs for truncated references.")]
    public List<FamilyTreePerson> People { get; set; } = [];

    [JsonPropertyName("kinship")]
    [Description("Has_relative entries surfaced via the kinship plugin. Null/empty when there are none.")]
    public List<FamilyTreeKinshipEntry>? Kinship { get; set; }

    [JsonPropertyName("limitations")]
    [Required]
    public FamilyTreeLimitations Limitations { get; set; } = new();
}

[Description(
    "Marriage-aware, generation-aligned family tree centered on a single Character. "
        + "Use for kinship questions: \"family tree\", \"lineage\", \"ancestry\", \"genealogy\", \"parent/child/spouse/sibling\", \"trace heritage\". "
        + "REQUIRED PRECONDITION: call search_entities first to resolve the PageId AND verify the resolved entity is a Character. "
        + "DO NOT use this tool for Family aggregates, Organizations, Governments, military command chains, or political hierarchies — those go to render_graph (Tree mode). "
        + "Family edge labels are fixed server-side; this tool takes no `labels` / `enabledLabels` parameters."
)]
public class FamilyTreeDescriptor
{
    [JsonPropertyName("title")]
    [Required]
    [Description("Descriptive title shown above the chart (e.g. \"Skywalker family tree centered on Anakin Skywalker\").")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("rootEntityId")]
    [Required]
    [Description("PageId of the focal Character (resolved via search_entities).")]
    public int RootEntityId { get; set; }

    [JsonPropertyName("rootEntityName")]
    [Required]
    [Description("The focal Character's display name.")]
    public string RootEntityName { get; set; } = string.Empty;

    [JsonPropertyName("maxDepth")]
    [Description("Generations to expand in each direction (ancestors + descendants). Clamped server-side to [1..5]. Default 3.")]
    public int MaxDepth { get; set; } = 3;

    [JsonPropertyName("continuity")]
    [Description("Optional continuity filter: Canon, Legends, or omit for both.")]
    public string? Continuity { get; set; }

    [JsonPropertyName("mobileSummary")]
    [Required]
    [Description(
        "Concise markdown text summary (3-6 bullets or short paragraphs) of the family tree's key relationships, lineage, and notable members. Shown to users on narrow viewports (< 960px) where the chart cannot be rendered legibly. Always populate this — it is the only thing mobile users will see in place of the chart. Use bullet points and **bold** key entities."
    )]
    public string MobileSummary { get; set; } = string.Empty;

    [JsonPropertyName("people")]
    [Required]
    [MinLength(1)]
    [Description("Person list verbatim from the FamilyTreeResponse — copied through, not transformed.")]
    public List<FamilyTreePerson> People { get; set; } = [];

    [JsonPropertyName("kinship")]
    [Description("Kinship list verbatim from the FamilyTreeResponse. Null/empty when there are none.")]
    public List<FamilyTreeKinshipEntry>? Kinship { get; set; }

    [JsonPropertyName("limitations")]
    [Required]
    public FamilyTreeLimitations Limitations { get; set; } = new();

    [JsonPropertyName("references")]
    [Description("Optional source references from wiki pages used to generate this result.")]
    public List<Reference>? References { get; set; }
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

/// <summary>
/// A source citation. Design-030 Phase 3: the preferred shape is just a
/// <see cref="PageId"/> — the server-side citation resolver turns that into a
/// name + the full set of navigation surfaces (Wiki / Graph / Galaxy Map /
/// Timeline / Holocron). The legacy <see cref="Title"/> + <see cref="Url"/>
/// pair is still accepted (and is the only option for non-KG sources), so old
/// persisted chat sessions keep rendering unchanged.
/// </summary>
[Description(
    "A source reference. Prefer passing the entity's pageId (from a KG tool result) and nothing else — the system resolves the name and links. Fall back to title+url only for sources with no pageId."
)]
[JsonConverter(typeof(ReferenceJsonConverter))]
public class Reference
{
    [JsonPropertyName("pageId")]
    [Description("The KG pageId of the cited entity, taken verbatim from a tool result. Preferred. Never invent one — if you don't have a pageId, use title+url instead.")]
    public int? PageId { get; set; }

    [JsonPropertyName("title")]
    [Description("Display title of the source page. Only needed when there is no pageId.")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    [Description("The Wookieepedia URL for the source page. Only needed when there is no pageId.")]
    public string? Url { get; set; }
}

/// <summary>
/// Tolerant converter for <see cref="Reference"/> — accepts the canonical object shape
/// <c>{ "pageId": N, "title": "...", "url": "..." }</c> AND the shorthand the model
/// frequently emits despite the schema: a bare integer <c>524426</c> which we lift into
/// <c>{ "pageId": 524426 }</c>, or a bare string URL we lift into <c>{ "url": "..." }</c>.
/// Without this, the model's "shorthand" args throw at deserialization and the whole
/// turn fails (the M.E.AI fallback string then trips the AGUI wire — see Design-041).
/// </summary>
public sealed class ReferenceJsonConverter : JsonConverter<Reference>
{
    public override Reference? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number when reader.TryGetInt32(out var id) => new Reference { PageId = id },
            JsonTokenType.String => new Reference { Url = reader.GetString() },
            JsonTokenType.StartObject => ReadObject(ref reader, options),
            _ => throw new JsonException($"Unexpected Reference token {reader.TokenType}"),
        };

    static Reference ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var r = new Reference();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var prop = reader.GetString();
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null)
                continue;
            switch (prop)
            {
                case "pageId"
                or "PageId":
                    r.PageId = reader.TokenType switch
                    {
                        JsonTokenType.Number => reader.GetInt32(),
                        JsonTokenType.String when int.TryParse(reader.GetString(), out var n) => n,
                        _ => null,
                    };
                    break;
                case "title"
                or "Title":
                    r.Title = reader.GetString();
                    break;
                case "url"
                or "Url":
                    r.Url = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return r;
    }

    public override void Write(Utf8JsonWriter writer, Reference value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.PageId is { } id)
            writer.WriteNumber("pageId", id);
        if (!string.IsNullOrEmpty(value.Title))
            writer.WriteString("title", value.Title);
        if (!string.IsNullOrEmpty(value.Url))
            writer.WriteString("url", value.Url);
        writer.WriteEndObject();
    }
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
