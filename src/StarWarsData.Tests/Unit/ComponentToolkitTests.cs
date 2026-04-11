using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Tests for <see cref="ComponentToolkit"/> — validates that descriptors produced by
/// the AI tool functions have valid configurations that won't crash the frontend
/// rendering components.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class ComponentToolkitTests
{
    [TestMethod]
    [DataRow("Bar")]
    [DataRow("Line")]
    [DataRow("StackedBar")]
    [DataRow("Radar")]
    public void RenderChart_BarLike_RequiresXAxisLabelsAndSeries(string chartType)
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: chartType, title: "Test Chart", xAxisLabels: ["A", "B", "C"], series: [new ChartSeries { Name = "S1", Data = [1, 2, 3] }]);

        Assert.IsNotNull(result);
        Assert.AreEqual("Test Chart", result.Title);
        Assert.IsNotNull(result.XAxisLabels);
        Assert.AreEqual(3, result.XAxisLabels!.Count);
        Assert.IsNotNull(result.Series);
        Assert.AreEqual(1, result.Series!.Count);
        Assert.AreEqual(3, result.Series![0].Data.Count);
    }

    [TestMethod]
    [DataRow("Pie")]
    [DataRow("Donut")]
    [DataRow("Rose")]
    public void RenderChart_PieLike_RequiresLabelsAndSeries(string chartType)
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: chartType, title: "Distribution", labels: ["Jedi", "Sith", "Mandalorian"], series: [new ChartSeries { Name = "Values", Data = [45, 30, 25] }]);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Labels);
        Assert.AreEqual(3, result.Labels!.Count);
        Assert.IsNotNull(result.Series);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual(AskChartType.TimeSeries, result.ChartType);
        Assert.IsNotNull(result.TimeSeries);
        Assert.AreEqual(1, result.TimeSeries!.Count);
        Assert.AreEqual(2, result.TimeSeries![0].Data.Count);
    }

    [TestMethod]
    public void RenderChart_InvalidChartType_DefaultsToBar()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "InvalidType", title: "Fallback", xAxisLabels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }]);

        Assert.AreEqual(AskChartType.Bar, result.ChartType);
    }

    [TestMethod]
    public void RenderChart_CaseInsensitiveParsing_Works()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "pie", title: "Case Test", labels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }]);

        Assert.AreEqual(AskChartType.Pie, result.ChartType);
    }

    [TestMethod]
    public void RenderChart_NullSeries_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "Bar", title: "Empty Chart");

        Assert.IsNotNull(result);
        Assert.IsNull(result.Series);
        Assert.IsNull(result.XAxisLabels);
    }

    [TestMethod]
    public void RenderChart_MismatchedLengths_StillCreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(chartType: "Bar", title: "Mismatched", xAxisLabels: ["A", "B"], series: [new ChartSeries { Name = "S1", Data = [1, 2, 3, 4] }]);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.XAxisLabels!.Count);
        Assert.AreEqual(4, result.Series![0].Data.Count);
    }

    [TestMethod]
    public void RenderChart_WithReferences_IncludesReferences()
    {
        var toolkit = new ComponentToolkit();

        var refs = new List<Reference>
        {
            new() { Title = "Wookieepedia", Url = "https://starwars.fandom.com" },
        };

        var result = toolkit.RenderChart(chartType: "Bar", title: "With Refs", xAxisLabels: ["A"], series: [new ChartSeries { Name = "S1", Data = [1] }], references: refs);

        Assert.IsNotNull(result.References);
        Assert.AreEqual(1, result.References!.Count);
    }

    [TestMethod]
    public void RenderChart_MultipleSeries_AllPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderChart(
            chartType: "StackedBar",
            title: "Multi-Series",
            xAxisLabels: ["2020", "2021", "2022"],
            series: [new ChartSeries { Name = "Canon", Data = [10, 20, 30] }, new ChartSeries { Name = "Legends", Data = [5, 15, 25] }, new ChartSeries { Name = "Both", Data = [3, 8, 12] }]
        );

        Assert.AreEqual(3, result.Series!.Count);
        Assert.AreEqual("Canon", result.Series![0].Name);
        Assert.AreEqual("Legends", result.Series![1].Name);
        Assert.AreEqual("Both", result.Series![2].Name);
    }

    [TestMethod]
    public void RenderTable_ValidConfig_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Characters", infoboxType: "Character", fields: ["Born", "Died", "Homeworld", "Species"]);

        Assert.IsNotNull(result);
        Assert.AreEqual("Characters", result.Title);
        Assert.AreEqual("Character", result.Collection);
        Assert.AreEqual(4, result.Fields.Count);
        Assert.AreEqual(25, result.PageSize);
    }

    [TestMethod]
    public void RenderTable_CustomPageSize_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Big Table", infoboxType: "Planet", fields: ["Region"], pageSize: 100);

        Assert.AreEqual(100, result.PageSize);
    }

    [TestMethod]
    public void RenderTable_WithSearch_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "Search Table", infoboxType: "Character", fields: ["Born"], search: "Skywalker");

        Assert.AreEqual("Skywalker", result.Search);
    }

    [TestMethod]
    public void RenderTable_EmptyFields_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTable(title: "No Fields", infoboxType: "Character", fields: []);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Fields.Count);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Columns.Count);
        Assert.AreEqual(2, result.Rows.Count);
        Assert.AreEqual("Jedi", result.Rows[0][0]);
    }

    [TestMethod]
    public void RenderDataTable_EmptyRows_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderDataTable(title: "Empty", columns: ["Name"], rows: []);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Rows.Count);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.RootEntityId);
        Assert.AreEqual("Luke Skywalker", result.RootEntityName);
        Assert.AreEqual(2, result.MaxDepth);
        Assert.AreEqual(4, result.Labels.Count);
        Assert.AreEqual(GraphLayoutMode.Tree, result.LayoutMode);
    }

    [TestMethod]
    public void RenderGraph_WithContinuity_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderGraph(rootEntityId: 100, rootEntityName: "Galactic Empire", title: "Empire Hierarchy", labels: ["head_of_state", "has_military_branch"], continuity: "Canon");

        Assert.IsNotNull(result);
        Assert.AreEqual("Canon", result.Continuity);
        Assert.AreEqual(2, result.Labels.Count);
    }

    [TestMethod]
    public void RenderGraph_CustomMaxDepth_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderGraph(rootEntityId: 1, rootEntityName: "Luke Skywalker", title: "Deep Tree", labels: ["child_of"], maxDepth: 5);

        Assert.AreEqual(5, result.MaxDepth);
    }

    [TestMethod]
    public void RenderTimeline_ValidConfig_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "Clone Wars Timeline", categories: ["Battle", "War"], yearFrom: 22, yearFromDemarcation: "BBY", yearTo: 19, yearToDemarcation: "BBY");

        Assert.IsNotNull(result);
        Assert.AreEqual("Clone Wars Timeline", result.Title);
        Assert.AreEqual(2, result.Categories.Count);
        Assert.AreEqual(22f, result.YearFrom);
        Assert.AreEqual("BBY", result.YearFromDemarcation);
        Assert.AreEqual(19f, result.YearTo);
        Assert.AreEqual("BBY", result.YearToDemarcation);
    }

    [TestMethod]
    public void RenderTimeline_NoYearRange_NullsArePreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "All Events", categories: ["Character"]);

        Assert.IsNull(result.YearFrom);
        Assert.IsNull(result.YearTo);
        Assert.IsNull(result.YearFromDemarcation);
        Assert.IsNull(result.YearToDemarcation);
    }

    [TestMethod]
    public void RenderTimeline_WithSearch_IsPreserved()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderTimeline(title: "Skywalker Events", categories: ["Character"], search: "Skywalker");

        Assert.AreEqual("Skywalker", result.Search);
    }

    [TestMethod]
    public void RenderInfobox_SinglePageId_CreatesDescriptor()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderInfobox(title: "Luke Skywalker", pageIds: [1]);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.PageIds.Count);
        Assert.AreEqual(1, result.PageIds[0]);
    }

    [TestMethod]
    public void RenderInfobox_MultiplePageIds_ForComparison()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderInfobox(title: "Yoda vs Dooku", pageIds: [1, 2, 3]);

        Assert.AreEqual(3, result.PageIds.Count);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Sections.Count);
        Assert.AreEqual("Overview", result.Sections[0].Heading);
        Assert.AreEqual(42, result.Sections[0].SourcePageId);
    }

    [TestMethod]
    public void RenderText_EmptySections_DoesNotCrash()
    {
        var toolkit = new ComponentToolkit();

        var result = toolkit.RenderText(title: "Empty", sections: []);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Sections.Count);
    }

    [TestMethod]
    public void OnlyOneResultIsSet_PerToolCall()
    {
        var toolkit = new ComponentToolkit();

        toolkit.RenderChart("Bar", "Test", ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);

        Assert.IsNotNull(toolkit.ChartResult);
        Assert.IsNull(toolkit.TableResult);
        Assert.IsNull(toolkit.DataTableResult);
        Assert.IsNull(toolkit.GraphResult);
        Assert.IsNull(toolkit.InfoboxResult);
        Assert.IsNull(toolkit.TextResult);
        Assert.IsNull(toolkit.TimelineResult);
    }

    [TestMethod]
    public void SequentialCalls_OverwritePreviousResult()
    {
        var toolkit = new ComponentToolkit();

        toolkit.RenderChart("Bar", "First", ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);
        Assert.AreEqual("First", toolkit.ChartResult!.Title);

        toolkit.RenderChart("Pie", "Second", labels: ["A"], series: [new ChartSeries { Name = "S", Data = [1] }]);
        Assert.AreEqual("Second", toolkit.ChartResult!.Title);
        Assert.AreEqual(AskChartType.Pie, toolkit.ChartResult!.ChartType);
    }

    [TestMethod]
    public void AsAIFunctions_ReturnsAllToolDefinitions()
    {
        var toolkit = new ComponentToolkit();
        var functions = toolkit.AsAIFunctions();

        Assert.AreEqual(9, functions.Count);
        var names = functions.Select(f => f.Name).ToHashSet();
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderTable));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderDataTable));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderChart));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderGraph));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderPath));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderTimeline));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderInfobox));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderMarkdown));
        Assert.IsTrue(names.Contains(ToolNames.Component.RenderAurebesh));
    }
}
