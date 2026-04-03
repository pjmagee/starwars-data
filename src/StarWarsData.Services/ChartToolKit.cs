using System.ComponentModel;
using Microsoft.Extensions.AI;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class ComponentToolkit
{
    public TableDescriptor? TableResult { get; private set; }
    public DataTableDescriptor? DataTableResult { get; private set; }
    public ChartDescriptor? ChartResult { get; private set; }
    public GraphDescriptor? GraphResult { get; private set; }

    [Description(
        "Render a paginated table browsing pages of a given infobox type. "
            + "The frontend fetches data from the API using the infoboxType to filter the 'Pages' collection by infobox.Template. "
            + "You do NOT need to query or include actual row data — just configure which type and fields to show."
    )]
    public TableDescriptor RenderTable(
        [Description("Descriptive title for the table")] string title,
        [Description(
            "The infobox type name (e.g. Character, Battle, Planet, ForcePower, Species, War, Food, Droid). "
                + "The frontend uses this to filter the 'Pages' collection by infobox.Template — it is NOT a MongoDB collection name."
        )]
            string infoboxType,
        [Description(
            "Which infobox.Data label names to show as columns (e.g. [\"Born\", \"Died\", \"Homeworld\", \"Species\"]). Pick 3-6 relevant fields."
        )]
            List<string> fields,
        [Description("Optional text search to filter results by page title")] string? search = null,
        [Description("Number of rows per page (default 25)")] int pageSize = 25,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        TableResult = new TableDescriptor
        {
            Title = title,
            Collection = infoboxType,
            Fields = fields,
            Search = search,
            PageSize = pageSize,
            References = references,
        };
        return TableResult;
    }

    [Description(
        "Render an ad-hoc data table with inline row data you provide. "
            + "Use when you have custom aggregation results, cross-type queries, temporal facet comparisons, "
            + "or computed data that doesn't map to a single infobox type. "
            + "Great for cross-temporal queries: e.g. 'Characters alive during the Clone Wars' — "
            + "query temporal facets, assemble rows with name, born, died, type. "
            + "You MUST query the data first and pass the actual rows."
    )]
    public DataTableDescriptor RenderDataTable(
        [Description("Descriptive title for the table")] string title,
        [Description("Column header names")] List<string> columns,
        [Description("Row data — each row is a list of string values in the same order as columns")]
            List<List<string>> rows,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        DataTableResult = new DataTableDescriptor
        {
            Title = title,
            Columns = columns,
            Rows = rows,
            References = references,
        };
        return DataTableResult;
    }

    [Description(
        "Render a chart with pre-computed aggregated data. "
            + "Use for counts, comparisons, distributions, or trends. "
            + "You MUST aggregate the data first using search/query tools, then pass the results here."
    )]
    public ChartDescriptor RenderChart(
        [Description("Bar, Line, Pie, Donut, Rose, StackedBar, TimeSeries, or Radar")]
            string chartType,
        [Description("Descriptive title for the chart")] string title,
        [Description(
            "For Bar/Line/StackedBar/Radar: category labels for the X-axis (Radar: axis names)"
        )]
            List<string>? xAxisLabels = null,
        [Description("For Pie/Donut/Rose: slice labels")] List<string>? labels = null,
        [Description(
            "For Bar/Line/StackedBar/Pie/Donut/Rose/Radar: array of { name, data: number[] }"
        )]
            List<ChartSeries>? series = null,
        [Description(
            "For TimeSeries: array of { name, data: [{ x: ISO-date-string, y: number }] }"
        )]
            List<TimeSeriesChartSeries>? timeSeries = null,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        if (!Enum.TryParse<AskChartType>(chartType, ignoreCase: true, out var parsedType))
            parsedType = AskChartType.Bar;

        ChartResult = new ChartDescriptor
        {
            Title = title,
            ChartType = parsedType,
            XAxisLabels = xAxisLabels,
            Labels = labels,
            Series = series,
            TimeSeries = timeSeries,
            References = references,
        };
        return ChartResult;
    }

    [Description(
        "Render a relationship graph powered by the knowledge graph (kg.edges). "
            + "The frontend fetches connected entities from the KG and renders a D3 visualization. "
            + "Call get_relationship_types(entityId) first to discover available edge labels for the entity. "
            + "Pass the relevant labels to focus the graph (e.g. child_of, parent_of for family trees; "
            + "head_of_state, has_military_branch for political hierarchies; affiliated_with for alliances). "
            + "Two layout modes: 'tree' renders a hierarchical top-down layout where depth is inferred from "
            + "the graph structure (root at top, direct connections below, etc.) — works for ANY entity type "
            + "(Characters, Governments, Organizations). 'force' (default) renders a physics-based network."
    )]
    public GraphDescriptor RenderGraph(
        [Description("The entity's PageId from the knowledge graph (from search_entities)")]
            int rootEntityId,
        [Description("The entity's display name")] string rootEntityName,
        [Description("Descriptive title for the graph")] string title,
        [Description(
            "KG edge labels to traverse. Call get_relationship_types(entityId) to discover available labels. "
                + "Examples: ['child_of', 'parent_of', 'partner_of'] for family trees, "
                + "['head_of_state', 'has_military_branch', 'has_executive_branch'] for org hierarchies, "
                + "['affiliated_with', 'member_of'] for alliance networks."
        )]
            List<string> labels,
        [Description(
            "How many hops to traverse from the root (default 2). Use 1 for direct relationships, 2-3 for deeper exploration."
        )]
            int maxDepth = 2,
        [Description(
            "Labels to show by default. Subset of labels. If omitted, all labels are enabled."
        )]
            List<string>? enabledLabels = null,
        [Description(
            "Layout mode: 'force' for physics-based network graph, 'tree' for hierarchical layout. "
                + "Use 'tree' for family trees, lineages, and organizational hierarchies."
        )]
            string layoutMode = "force",
        [Description("Optional continuity filter: Canon, Legends, or omit for all")]
            string? continuity = null,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        GraphResult = new GraphDescriptor
        {
            Title = title,
            RootEntityId = rootEntityId,
            RootEntityName = rootEntityName,
            MaxDepth = maxDepth,
            Labels = labels,
            EnabledLabels = enabledLabels,
            LayoutMode = layoutMode is "tree" or "force" ? layoutMode : "force",
            Continuity = continuity,
            References = references,
        };
        return GraphResult;
    }

    public InfoboxDescriptor? InfoboxResult { get; private set; }
    public TextDescriptor? TextResult { get; private set; }
    public TimelineDescriptor? TimelineResult { get; private set; }

    [Description(
        "Render a timeline of events. Use for temporal queries like 'battles during the Clone Wars' or 'what happened between 20 BBY and 4 ABY'. "
            + "The frontend fetches paginated timeline data from the timeline.* collections. "
            + "You do NOT need to query timeline events yourself — just provide the category names. "
            + "For entity-specific timelines, use get_entity_timeline first to read the entity's temporal facets "
            + "(lifespan.start/end, conflict.start/end, etc.), then pass the year range from those facets to yearFrom/yearTo. "
            + "Use the search parameter to further filter by entity name in event titles."
    )]
    public TimelineDescriptor RenderTimeline(
        [Description("Descriptive title for the timeline")] string title,
        [Description(
            "Timeline event category names from timeline.* collections (e.g. [\"Battle\", \"War\", \"Character\"]). "
                + "Call list_timeline_categories first to discover valid names."
        )]
            List<string> categories,
        [Description("Number of year groups per page (default 15)")] int pageSize = 15,
        [Description(
            "Start of year range (e.g. 41 for 41 BBY). Use to scope timeline to a specific period."
        )]
            float? yearFrom = null,
        [Description("Demarcation for yearFrom: 'BBY' or 'ABY'")]
            string? yearFromDemarcation = null,
        [Description(
            "End of year range (e.g. 4 for 4 ABY). Use to scope timeline to a specific period."
        )]
            float? yearTo = null,
        [Description("Demarcation for yearTo: 'BBY' or 'ABY'")] string? yearToDemarcation = null,
        [Description(
            "Optional text to filter timeline event titles (e.g. entity name like 'Skywalker')"
        )]
            string? search = null,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        TimelineResult = new TimelineDescriptor
        {
            Title = title,
            Categories = categories,
            PageSize = pageSize,
            YearFrom = yearFrom,
            YearFromDemarcation = yearFromDemarcation,
            YearTo = yearTo,
            YearToDemarcation = yearToDemarcation,
            Search = search,
            References = references,
        };
        return TimelineResult;
    }

    [Description(
        "Render one or more wiki-style infobox cards for specific pages. "
            + "Use when the user asks about a specific entity (e.g. 'tell me about Mace Windu', 'bring up Darth Vader'). "
            + "You MUST search for the entity first using search_pages_by_name to get the PageId(s). "
            + "Can render multiple cards side-by-side for comparisons."
    )]
    public InfoboxDescriptor RenderInfobox(
        [Description("Descriptive title (e.g. 'Mace Windu' or 'Yoda vs Count Dooku')")]
            string title,
        [Description(
            "One or more PageId integers to display as infobox cards. Use search_pages_by_name to find these."
        )]
            List<int> pageIds,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        InfoboxResult = new InfoboxDescriptor
        {
            Title = title,
            PageIds = pageIds,
            References = references,
        };
        return InfoboxResult;
    }

    [Description(
        "Render markdown-formatted text content — articles, summaries, analysis, or RAG results. "
            + "The frontend renders content with a full markdown component (MudMarkdown). "
            + "You MUST write section content as proper markdown: use ## headings, **bold**, "
            + "bullet lists (blank line before the list), [links](url), > blockquotes, and `code`. "
            + "Raw unformatted text looks bad — always use markdown structure. "
            + "Fetch page data first using get_page_by_id, search_article_content, or KG tools, then write sections. "
            + "Can combine text from multiple sources for comprehensive answers."
    )]
    public TextDescriptor RenderText(
        [Description("Descriptive title for the text section")] string title,
        [Description(
            "Text sections to display. Each section has: heading (plain text title), "
                + "content (markdown-formatted body — use headings, bold, lists, links), "
                + "and optional sourcePageTitle for attribution."
        )]
            List<TextSection> sections,
        [Description(
            "Optional source references (title + wikiUrl) from pages used to answer this query"
        )]
            List<Reference>? references = null
    )
    {
        TextResult = new TextDescriptor
        {
            Title = title,
            Sections = sections,
            References = references,
        };
        return TextResult;
    }

    public List<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(RenderTable, "render_table"),
            AIFunctionFactory.Create(RenderDataTable, "render_data_table"),
            AIFunctionFactory.Create(RenderChart, "render_chart"),
            AIFunctionFactory.Create(RenderGraph, "render_graph"),
            AIFunctionFactory.Create(RenderTimeline, "render_timeline"),
            AIFunctionFactory.Create(RenderInfobox, "render_infobox"),
            AIFunctionFactory.Create(RenderText, "render_markdown"),
        ];
}
