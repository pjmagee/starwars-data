using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.Tests;

/// <summary>
/// Tests for <see cref="ComponentToolkit"/> — validates that descriptors produced by
/// the AI tool functions have valid configurations that won't crash the frontend
/// rendering components (AskChartView, AskTableView, etc.).
///
/// These test the contract between AI-generated tool calls and the Blazor components
/// that consume the resulting descriptors.
/// </summary>
public class ComponentToolkitTests
{
    // ── RenderChart ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Bar")]
    [InlineData("Line")]
    [InlineData("StackedBar")]
    [InlineData("Radar")]
    public void RenderChart_BarLike_RequiresXAxisLabelsAndSeries(string chartType)
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: chartType, title: "Test Chart", xAxisLabels: ["A", "B", "C"], series: [new ChartSeries { Name = "S1", Data = [1, 2, 3] }]);

        Assert.NotNull(result);
        Assert.Equal("Test Chart", result.Title);
        Assert.NotNull(result.XAxisLabels);
        Assert.Equal(3, result.XAxisLabels!.Count);
        Assert.NotNull(result.Series);
        Assert.Single(result.Series!);
        Assert.Equal(3, result.Series![0].Data.Count);
    }

    [Theory]
    [InlineData("Pie")]
    [InlineData("Donut")]
    [InlineData("Rose")]
    public void RenderChart_PieLike_RequiresLabelsAndSeries(string chartType)
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: chartType, title: "Distribution", labels: ["Jedi", "Sith", "Mandalorian"], series: [new ChartSeries { Name = "Values", Data = [45, 30, 25] }]);

        Assert.NotNull(result);
        Assert.NotNull(result.Labels);
        Assert.Equal(3, result.Labels!.Count);
        Assert.NotNull(result.Series);
    }

    [Fact]
    public void RenderChart_TimeSeries_RequiresTimeSeriesData()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(
            chartType: "TimeSeries",
            title: "Over Time",
            timeSeries:
            [
                new TimeSeriesChartSeries
                {
                    Name = "Events",
                    Data = [new TimeSeriesDataPoint { X = new DateTime(2024, 1, 1), Y = 10 }, new TimeSeriesDataPoint { X = new DateTime(2024, 6, 1), Y = 20 }],
                },
            ]
        );

        Assert.NotNull(result);
        Assert.Equal(AskChartType.TimeSeries, result.ChartType);
        Assert.NotNull(result.TimeSeries);
        Assert.Single(result.TimeSeries!);
        Assert.Equal(2, result.TimeSeries![0].Data.Count);
    }

    [Fact]
    public void RenderChart_InvalidChartType_DefaultsToBar()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "InvalidType", title: "Fallback", xAxisLabels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }]);

        Assert.Equal(AskChartType.Bar, result.ChartType);
    }

    [Fact]
    public void RenderChart_CaseInsensitiveParsing_Works()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "pie", title: "Case Test", labels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }]);

        Assert.Equal(AskChartType.Pie, result.ChartType);
    }

    [Fact]
    public void RenderChart_NullSeries_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        // AI might send a chart with no series data — shouldn't crash
        var result = toolkit.RenderChart(chartType: "Bar", title: "Empty Chart");

        Assert.NotNull(result);
        Assert.Null(result.Series);
        Assert.Null(result.XAxisLabels);
    }

    [Fact]
    public void RenderChart_MismatchedLengths_StillCreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        // AI might pass mismatched label/data counts — toolkit shouldn't validate,
        // but the descriptor should be created for the frontend to handle
        var result = toolkit.RenderChart(chartType: "Bar", title: "Mismatched", xAxisLabels: ["A", "B"], series: [new ChartSeries { Name = "S1", Data = [1, 2, 3, 4] }]); // 4 values, 2 labels

        Assert.NotNull(result);
        Assert.Equal(2, result.XAxisLabels!.Count);
        Assert.Equal(4, result.Series![0].Data.Count);
    }

    [Fact]
    public void RenderChart_WithReferences_IncludesReferences()
    {
        var toolkit = new ComponentToolkit();

        var refs = new List<Reference>
        {
            new() { Title = "Wookieepedia", Url = "https://starwars.fandom.com" },
        };

        var result = toolkit.RenderChart(chartType: "Bar", title: "With Refs", xAxisLabels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }], references: refs);

        Assert.NotNull(result.References);
        Assert.Single(result.References!);
    }

    [Fact]
    public void RenderChart_MultipleSeries_AllPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(
            chartType: "StackedBar",
            title: "Multi-Series",
            xAxisLabels: ["2020", "2021", "2022"],
            series: [new ChartSeries { Name = "Canon", Data = [10, 20, 30] }, new ChartSeries { Name = "Legends", Data = [5, 15, 25] }, new ChartSeries { Name = "Both", Data = [3, 8, 12] }]
        );

        Assert.Equal(3, result.Series!.Count);
        Assert.Equal("Canon", result.Series![0].Name);
        Assert.Equal("Legends", result.Series![1].Name);
        Assert.Equal("Both", result.Series![2].Name);
    }

    // ── RenderTable ───────────────────────────────────────────────────────

    [Fact]
    public void RenderTable_ValidConfig_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Characters", infoboxType: "Character", fields: ["Born", "Died", "Homeworld", "Species"]);

        Assert.NotNull(result);
        Assert.Equal("Characters", result.Title);
        Assert.Equal("Character", result.Collection);
        Assert.Equal(4, result.Fields.Count);
        Assert.Equal(25, result.PageSize); // default
    }

    [Fact]
    public void RenderTable_CustomPageSize_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Big Table", infoboxType: "Planet", fields: ["Region"], pageSize: 100);

        Assert.Equal(100, result.PageSize);
    }

    [Fact]
    public void RenderTable_WithSearch_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Search Table", infoboxType: "Character", fields: ["Born"], search: "Skywalker");

        Assert.Equal("Skywalker", result.Search);
    }

    [Fact]
    public void RenderTable_EmptyFields_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "No Fields", infoboxType: "Character", fields: []);

        Assert.NotNull(result);
        Assert.Empty(result.Fields);
    }

    // ── RenderDataTable ───────────────────────────────────────────────────

    [Fact]
    public void RenderDataTable_ValidData_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderDataTable(
            title: "Custom Data",
            columns: ["Name", "Count"],
            rows:
            [
                ["Jedi", "42"],
                ["Sith", "27"],
            ]
        );

        Assert.NotNull(result);
        Assert.Equal(2, result.Columns.Count);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Jedi", result.Rows[0][0]);
    }

    [Fact]
    public void RenderDataTable_EmptyRows_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderDataTable(title: "Empty", columns: ["Name"], rows: []);

        Assert.NotNull(result);
        Assert.Empty(result.Rows);
    }

    // ── RenderGraph ───────────────────────────────────────────────────────

    [Fact]
    public void RenderGraph_ValidConfig_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderGraph(
            rootEntityId: 1,
            rootEntityName: "Luke Skywalker",
            title: "Skywalker Family Tree",
            labels: ["child_of", "parent_of", "partner_of", "sibling_of"],
            layoutMode: "tree"
        );

        Assert.NotNull(result);
        Assert.Equal(1, result.RootEntityId);
        Assert.Equal("Luke Skywalker", result.RootEntityName);
        Assert.Equal(2, result.MaxDepth); // default
        Assert.Equal(4, result.Labels.Count);
        Assert.Equal(GraphLayoutMode.Tree, result.LayoutMode);
    }

    [Fact]
    public void RenderGraph_WithContinuity_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderGraph(rootEntityId: 100, rootEntityName: "Galactic Empire", title: "Empire Hierarchy", labels: ["head_of_state", "has_military_branch"], continuity: "Canon");

        Assert.NotNull(result);
        Assert.Equal("Canon", result.Continuity);
        Assert.Equal(2, result.Labels.Count);
    }

    [Fact]
    public void RenderGraph_CustomMaxDepth_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderGraph(rootEntityId: 1, rootEntityName: "Luke Skywalker", title: "Deep Tree", labels: ["child_of"], maxDepth: 5);

        Assert.Equal(5, result.MaxDepth);
    }

    // ── RenderTimeline ────────────────────────────────────────────────────

    [Fact]
    public void RenderTimeline_ValidConfig_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "Clone Wars Timeline", categories: ["Battle", "War"], yearFrom: 22, yearFromDemarcation: "BBY", yearTo: 19, yearToDemarcation: "BBY");

        Assert.NotNull(result);
        Assert.Equal("Clone Wars Timeline", result.Title);
        Assert.Equal(2, result.Categories.Count);
        Assert.Equal(22f, result.YearFrom);
        Assert.Equal("BBY", result.YearFromDemarcation);
        Assert.Equal(19f, result.YearTo);
        Assert.Equal("BBY", result.YearToDemarcation);
    }

    [Fact]
    public void RenderTimeline_NoYearRange_NullsArePreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "All Events", categories: ["Character"]);

        Assert.Null(result.YearFrom);
        Assert.Null(result.YearTo);
        Assert.Null(result.YearFromDemarcation);
        Assert.Null(result.YearToDemarcation);
    }

    [Fact]
    public void RenderTimeline_WithSearch_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "Skywalker Events", categories: ["Character"], search: "Skywalker");

        Assert.Equal("Skywalker", result.Search);
    }

    // ── RenderInfobox ─────────────────────────────────────────────────────

    [Fact]
    public void RenderInfobox_SinglePageId_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderInfobox(title: "Luke Skywalker", pageIds: [1]);

        Assert.NotNull(result);
        Assert.Single(result.PageIds);
        Assert.Equal(1, result.PageIds[0]);
    }

    [Fact]
    public void RenderInfobox_MultiplePageIds_ForComparison()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderInfobox(title: "Yoda vs Dooku", pageIds: [1, 2, 3]);

        Assert.Equal(3, result.PageIds.Count);
    }

    // ── RenderText ────────────────────────────────────────────────────────

    [Fact]
    public void RenderText_WithSections_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderText(
            title: "The Force",
            sections:
            [
                new TextSection
                {
                    Heading = "Overview",
                    Content = "The Force is an energy field.",
                    SourcePageId = 42,
                    SourcePageTitle = "The Force",
                },
            ]
        );

        Assert.NotNull(result);
        Assert.Single(result.Sections);
        Assert.Equal("Overview", result.Sections[0].Heading);
        Assert.Equal(42, result.Sections[0].SourcePageId);
    }

    [Fact]
    public void RenderText_EmptySections_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderText(title: "Empty", sections: []);

        Assert.NotNull(result);
        Assert.Empty(result.Sections);
    }

    // ── Result properties set correctly ───────────────────────────────────

    [Fact]
    public void OnlyOneResultIsSet_PerToolCall()
    {
        var toolkit = new ComponentToolkit();

        toolkit.RenderChart("Bar", "Test", ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);

        Assert.NotNull(toolkit.ChartResult);
        Assert.Null(toolkit.TableResult);
        Assert.Null(toolkit.DataTableResult);
        Assert.Null(toolkit.GraphResult);
        Assert.Null(toolkit.InfoboxResult);
        Assert.Null(toolkit.TextResult);
        Assert.Null(toolkit.TimelineResult);
    }

    [Fact]
    public void SequentialCalls_OverwritePreviousResult()
    {
        var toolkit = new ComponentToolkit();

        toolkit.RenderChart("Bar", "First", ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);
        Assert.Equal("First", toolkit.ChartResult!.Title);

        toolkit.RenderChart("Pie", "Second", labels: ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);
        Assert.Equal("Second", toolkit.ChartResult!.Title);
        Assert.Equal(AskChartType.Pie, toolkit.ChartResult!.ChartType);
    }

    // ── AsAIFunctions ─────────────────────────────────────────────────────

    [Fact]
    public void AsAIFunctions_ReturnsAllToolDefinitions()
    {
        var toolkit = new ComponentToolkit();
        var functions = toolkit.AsAIFunctions();

        Assert.Equal(9, functions.Count);
        var names = functions.Select(f => f.Name).ToHashSet();
        Assert.Contains("render_table", names);
        Assert.Contains("render_data_table", names);
        Assert.Contains("render_chart", names);
        Assert.Contains("render_graph", names);
        Assert.Contains("render_path", names);
        Assert.Contains("render_timeline", names);
        Assert.Contains("render_infobox", names);
        Assert.Contains("render_markdown", names);
        Assert.Contains("render_aurebesh", names);
    }
}
