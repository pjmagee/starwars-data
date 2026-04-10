using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// AI tools that emit UI component descriptors (tables, charts, graphs, timelines,
/// infoboxes, markdown, Aurebesh) consumed by the Blazor frontend.
///
/// These tools do not fetch data themselves — the agent gathers facts with the
/// other toolkits (DataExplorer, GraphRAG, KGAnalytics) and then invokes one of
/// these render_* tools to describe HOW the answer should be presented.
///
/// Every descriptor accepts an optional <c>references</c> list so the frontend
/// can surface source attribution (title + wikiUrl) for the pages used.
/// </summary>
public class ComponentToolkit
{
    public TableDescriptor? TableResult { get; private set; }
    public DataTableDescriptor? DataTableResult { get; private set; }
    public ChartDescriptor? ChartResult { get; private set; }
    public GraphDescriptor? GraphResult { get; private set; }
    public InfoboxDescriptor? InfoboxResult { get; private set; }
    public TextDescriptor? TextResult { get; private set; }
    public TimelineDescriptor? TimelineResult { get; private set; }

    const string ReferencesParamDescription = "Optional source references (title + wikiUrl) from pages used to answer this query.";

    const string MobileSummaryParamDescription =
        "REQUIRED for mobile users. Concise markdown summary (3-6 bullet points or short paragraphs) of "
        + "the visualization's key insights — top items, distribution, outliers, key facts. Shown to users on "
        + "narrow viewports (< 960px) where the chart/graph/table cannot be rendered legibly. This is the "
        + "ONLY thing mobile users will see in place of the visual, so include the actual numeric values, "
        + "key entity names in **bold**, and the takeaway. Always populate this — never leave it null.";

    [Description(
        """
            Render a paginated table browsing pages of a given infobox type.
            The frontend fetches row data from the API using infoboxType to filter Pages by infobox.Template.
            Do NOT query or pass row data yourself — just configure which type and fields to show.
            """
    )]
    public TableDescriptor RenderTable(
        [Description("Descriptive title for the table")] string title,
        [Description("Infobox type name, e.g. Character, Battle, Planet, ForcePower, Species, War, Food, Droid.")] string infoboxType,
        [Description("infobox.Data label names to show as columns, e.g. [\"Born\", \"Died\", \"Homeworld\", \"Species\"]. Pick 3-6 relevant fields.")] List<string> fields,
        [Description("Optional text search to filter results by page title")] string? search = null,
        [Description("Number of rows per page (default 25)")] int pageSize = 25,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        TableResult = new TableDescriptor
        {
            Title = title,
            Collection = infoboxType,
            Fields = fields,
            Search = search,
            PageSize = pageSize,
            MobileSummary = mobileSummary,
            References = references,
        };
        return TableResult;
    }

    [Description(
        """
            Render an ad-hoc data table with inline row data you provide.
            Use for custom aggregation results, cross-type queries, temporal facet comparisons,
            or computed data that doesn't map to a single infobox type.
            You MUST query the data first and pass the actual rows.
            """
    )]
    public DataTableDescriptor RenderDataTable(
        [Description("Descriptive title for the table")] string title,
        [Description("Column header names")] List<string> columns,
        [Description("Row data — each row is a list of string values in the same order as columns")] List<List<string>> rows,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        DataTableResult = new DataTableDescriptor
        {
            Title = title,
            Columns = columns,
            Rows = rows,
            MobileSummary = mobileSummary,
            References = references,
        };
        return DataTableResult;
    }

    [Description(
        """
            Render a chart with pre-computed aggregated data for counts, comparisons, distributions, or trends.
            You MUST aggregate the data first (use the KGAnalytics tools) and pass the results here.
            """
    )]
    public ChartDescriptor RenderChart(
        [Description("Bar, Line, Pie, Donut, Rose, StackedBar, TimeSeries, or Radar")] string chartType,
        [Description("Descriptive title for the chart")] string title,
        [Description("For Bar/Line/StackedBar/Radar: category labels for the X-axis (Radar: axis names)")] List<string>? xAxisLabels = null,
        [Description("For Pie/Donut/Rose: slice labels")] List<string>? labels = null,
        [Description("For Bar/Line/StackedBar/Pie/Donut/Rose/Radar: array of { name, data: number[] }")] List<ChartSeries>? series = null,
        [Description("For TimeSeries: array of { name, data: [{ x: ISO-date-string, y: number }] }")] List<TimeSeriesChartSeries>? timeSeries = null,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
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
            MobileSummary = mobileSummary,
            References = references,
        };
        return ChartResult;
    }

    [Description(
        """
            Render a relationship graph powered by the knowledge graph (kg.edges).
            Call get_relationship_types(entityId) first to discover available edge labels, then pass
            ONLY the labels relevant to the question — not every available label.
            Typical label sets:
              family trees      → child_of, parent_of, partner_of, sibling_of
              political orgs    → head_of_state, has_military_branch, has_executive_branch, commander_in_chief
              alliances         → affiliated_with, member_of
            Layout modes:
              Tree  = hierarchical top-down (works for any entity type: Characters, Governments, Organizations)
              Force = physics-based network for general exploration (default)
            Do NOT use this for shortest-path results — use render_path instead.
            """
    )]
    public GraphDescriptor RenderGraph(
        [Description("The entity's PageId from the knowledge graph (from search_entities)")] int rootEntityId,
        [Description("The entity's display name")] string rootEntityName,
        [Description("Descriptive title for the graph")] string title,
        [Description("KG edge labels to traverse. Pass only labels relevant to the question, not all available labels.")] List<string> labels,
        [Description("Traversal depth from the root. 1 = direct only, 2 = default (direct + one hop), 3 = deeper hierarchies/family trees.")] int maxDepth = 2,
        [Description("Subset of labels to enable by default in the UI. If omitted, all labels are enabled.")] List<string>? enabledLabels = null,
        [Description("'Tree' for hierarchical top-down (family trees, org charts, political hierarchies), 'Force' for physics-based network (default).")] string layoutMode = "Force",
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        if (!Enum.TryParse<GraphLayoutMode>(layoutMode, ignoreCase: true, out var parsedMode))
            parsedMode = GraphLayoutMode.Force;

        // Path mode is only valid via render_path — clamp to Force if misused here
        if (parsedMode == GraphLayoutMode.Path)
            parsedMode = GraphLayoutMode.Force;

        GraphResult = new GraphDescriptor
        {
            Title = title,
            RootEntityId = rootEntityId,
            RootEntityName = rootEntityName,
            MaxDepth = maxDepth,
            Labels = labels,
            EnabledLabels = enabledLabels,
            LayoutMode = parsedMode,
            Continuity = continuity,
            MobileSummary = mobileSummary,
            References = references,
        };
        return GraphResult;
    }

    [Description(
        """
            Render a focused path graph showing only the shortest connection between two entities.
            Call find_connections() first to discover the path, then pass the path steps here.
            The frontend renders only the specified nodes and edges — no BFS expansion.
            Nodes are arranged left-to-right in chain order.
            """
    )]
    public GraphDescriptor RenderPath(
        [Description("Descriptive title for the path graph")] string title,
        [Description("PageId of the starting entity")] int fromEntityId,
        [Description("Display name of the starting entity")] string fromEntityName,
        [Description("PageId of the ending entity")] int toEntityId,
        [Description("Display name of the ending entity")] string toEntityName,
        [Description("Path steps from find_connections(). Each step: { fromId, fromName, fromType, toId, toName, toType, label, evidence }")] List<PathStepInput> pathSteps,
        [Description("Optional continuity filter: Canon, Legends, or omit for all")] string? continuity = null,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        GraphResult = new GraphDescriptor
        {
            Title = title,
            RootEntityId = fromEntityId,
            RootEntityName = fromEntityName,
            MaxDepth = pathSteps.Count,
            Labels = pathSteps.Select(s => s.Label).Distinct().ToList(),
            LayoutMode = GraphLayoutMode.Path,
            Continuity = continuity,
            MobileSummary = mobileSummary,
            PathData = new PathData
            {
                FromId = fromEntityId,
                FromName = fromEntityName,
                ToId = toEntityId,
                ToName = toEntityName,
                Steps = pathSteps
                    .Select(s => new PathStep
                    {
                        FromId = s.FromId,
                        FromName = s.FromName,
                        FromType = s.FromType,
                        ToId = s.ToId,
                        ToName = s.ToName,
                        ToType = s.ToType,
                        Label = s.Label,
                        Evidence = s.Evidence,
                    })
                    .ToList(),
            },
            References = references,
        };
        return GraphResult;
    }

    [Description(
        """
            Render a timeline of events. Supports two calendar modes:

            • Galactic (default) — in-universe BBY/ABY dates, e.g. "battles during the Clone Wars"
              or "what happened between 20 BBY and 4 ABY". Use yearFrom/yearTo as magnitudes
              paired with yearFromDemarcation/yearToDemarcation set to "BBY" or "ABY".
              Typical categories: Battle, War, Character, Era, Treaty, Government.

            • Real — real-world publication/release dates for Star Wars media, e.g.
              "publication history of canonical Star Wars across all media" or
              "films released between 1977 and 2005". Set calendar="Real" and pass yearFrom/yearTo
              as signed CE ints (negative for BCE). The Demarcation parameters are ignored.
              Typical categories: Book, Film, Video_game, Television_series, Comic_book.

            The frontend fetches paginated timeline data from timeline.* collections filtered by
            the requested calendar — you only provide the category names and range.
            For entity-specific timelines, call get_entity_timeline first to read the entity's
            temporal facets, then pass the resulting year range to yearFrom/yearTo.
            """
    )]
    public TimelineDescriptor RenderTimeline(
        [Description("Descriptive title for the timeline")] string title,
        [Description(
            "Timeline event category names. Galactic examples: [\"Battle\", \"War\", \"Character\"]. Real examples: [\"Book\", \"Film\", \"Video_game\"]. Call list_timeline_categories first to discover valid names."
        )]
            List<string> categories,
        [Description("Number of year groups per page (default 15)")] int pageSize = 15,
        [Description("Calendar mode: 'Galactic' (default, in-universe BBY/ABY) or 'Real' (real-world CE publication dates).")] string? calendar = null,
        [Description("Start of year range. Galactic: magnitude e.g. 41 with yearFromDemarcation='BBY'. Real: signed CE year e.g. 1977 (negative for BCE).")] float? yearFrom = null,
        [Description("Demarcation for yearFrom: 'BBY' or 'ABY'. Galactic mode only — omit when calendar='Real'.")] string? yearFromDemarcation = null,
        [Description("End of year range. Galactic: magnitude e.g. 4 with yearToDemarcation='ABY'. Real: signed CE year e.g. 2020.")] float? yearTo = null,
        [Description("Demarcation for yearTo: 'BBY' or 'ABY'. Galactic mode only — omit when calendar='Real'.")] string? yearToDemarcation = null,
        [Description("Optional text to filter timeline event titles (e.g. entity name like 'Skywalker')")] string? search = null,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        // Normalise the calendar argument so downstream string-compares are stable.
        // Unknown values fall back to null (server default = Galactic) rather than erroring
        // so a slightly off-spec model response still produces a valid timeline.
        var normalizedCalendar = calendar switch
        {
            null => null,
            _ when string.Equals(calendar, "Real", StringComparison.OrdinalIgnoreCase) => "Real",
            _ when string.Equals(calendar, "Galactic", StringComparison.OrdinalIgnoreCase) => "Galactic",
            _ => null,
        };

        TimelineResult = new TimelineDescriptor
        {
            Title = title,
            Categories = categories,
            PageSize = pageSize,
            Calendar = normalizedCalendar,
            YearFrom = yearFrom,
            YearFromDemarcation = yearFromDemarcation,
            YearTo = yearTo,
            YearToDemarcation = yearToDemarcation,
            Search = search,
            MobileSummary = mobileSummary,
            References = references,
        };
        return TimelineResult;
    }

    [Description(
        """
            Render one or more wiki-style infobox cards for specific pages.
            Use when the user asks about a specific entity ("tell me about Mace Windu", "bring up Darth Vader").
            Call search_pages_by_name first to resolve the PageId(s). Multiple cards render side-by-side for comparisons.
            """
    )]
    public InfoboxDescriptor RenderInfobox(
        [Description("Descriptive title, e.g. 'Mace Windu' or 'Yoda vs Count Dooku'")] string title,
        [Description("One or more PageId integers to display as infobox cards. Use search_pages_by_name to find these.")] List<int> pageIds,
        [Description(ReferencesParamDescription)] List<Reference>? references = null,
        [Description(MobileSummaryParamDescription)] string mobileSummary = ""
    )
    {
        InfoboxResult = new InfoboxDescriptor
        {
            Title = title,
            PageIds = pageIds,
            MobileSummary = mobileSummary,
            References = references,
        };
        return InfoboxResult;
    }

    [Description(
        """
            Render markdown-formatted text content — articles, summaries, analysis, or RAG results.
            The frontend renders content via MudMarkdown, so write proper markdown:
              ## headings, **bold**, bullet lists (blank line before), [links](url), > blockquotes, `code`.
            For hyperlinks, use wikiUrl from tool results, e.g. [Darth Vader](https://starwars.fandom.com/wiki/Darth_Vader).
            Fetch page data first via get_page_by_id, semantic_search, or KG tools, then write sections.
            """
    )]
    public TextDescriptor RenderText(
        [Description("Descriptive title for the text section")] string title,
        [Description("Text sections to display. Each has: heading (plain title), content (markdown body), and optional sourcePageTitle for attribution.")] List<TextSection> sections,
        [Description(ReferencesParamDescription)] List<Reference>? references = null
    )
    {
        // Sanitize markdown content from common AI formatting issues
        foreach (var section in sections)
        {
            if (section.Content is not null)
                section.Content = SanitizeMarkdown(section.Content);
        }

        TextResult = new TextDescriptor
        {
            Title = title,
            Sections = sections,
            References = references,
        };
        return TextResult;
    }

    /// <summary>
    /// Fix common AI markdown formatting issues so MudMarkdown renders correctly.
    /// </summary>
    static string SanitizeMarkdown(string md)
    {
        // Normalize line endings to \n
        md = md.Replace("\r\n", "\n").Replace("\r", "\n");

        // Strip zero-width Unicode characters that break emphasis/list parsing
        md = Regex.Replace(md, "[\u200B\u200C\u200D\uFEFF\u00AD]", "");

        // Strip code fence wrappers if the AI wrapped the whole content in ```markdown
        md = Regex.Replace(md, @"^\s*```\s*(?:markdown|md)?\s*\n([\s\S]*?)\n\s*```\s*$", "$1");

        // Ensure blank line before bullet lists (required for CommonMark)
        md = Regex.Replace(md, @"([^\n])\n(- |\* |\d+\. )", "$1\n\n$2");

        // Ensure blank line before bold-text sub-headings on their own line
        // e.g. "some text\n**Heading**\n" → "some text\n\n**Heading**\n"
        md = Regex.Replace(md, @"([^\n])\n(\*\*[^*\n]+\*\*)\n", "$1\n\n$2\n");

        // Fix headings missing space after # (e.g. ##Title → ## Title)
        md = Regex.Replace(md, @"^(#{1,6})([^ #\n])", "$1 $2", RegexOptions.Multiline);

        // Fix broken links with space before paren: [text] (url) → [text](url)
        md = Regex.Replace(md, @"\]\s+\(", "](");

        return md;
    }

    [Description(
        """
            Auto-convert English text to Aurebesh (the Star Wars alphabet).
            Write everything in plain English — the frontend converts it to Aurebesh visually.
            Do NOT attempt to write Aurebesh characters yourself. Supports full markdown.
            """
    )]
    public AurebeshDescriptor RenderAurebesh(
        [Description("Title shown above the Aurebesh output (displayed in normal English font)")] string title,
        [Description("Plain English text (with optional markdown). Rendered as Aurebesh by the frontend — do NOT transliterate.")] string text,
        [Description("Optional source references")] List<Reference>? references = null
    )
    {
        if (text is not null)
            text = SanitizeMarkdown(text);

        return new AurebeshDescriptor
        {
            Title = title,
            Text = text ?? "",
            References = references,
        };
    }

    public List<AITool> AsAIFunctions() =>
        [
            AIFunctionFactory.Create(RenderTable, "render_table"),
            AIFunctionFactory.Create(RenderDataTable, "render_data_table"),
            AIFunctionFactory.Create(RenderChart, "render_chart"),
            AIFunctionFactory.Create(RenderGraph, "render_graph"),
            AIFunctionFactory.Create(RenderPath, "render_path"),
            AIFunctionFactory.Create(RenderTimeline, "render_timeline"),
            AIFunctionFactory.Create(RenderInfobox, "render_infobox"),
            AIFunctionFactory.Create(RenderText, "render_markdown"),
            AIFunctionFactory.Create(RenderAurebesh, "render_aurebesh"),
        ];
}
