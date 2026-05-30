using System.Text.Json;
using Microsoft.Extensions.AI;
using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Regression coverage for the Design-041 "malformed response" failure: the model emits
/// scalar JSON shapes (years-as-numbers in chart labels, numeric/bool cells in data-table
/// rows, null gaps in series data) that System.Text.Json rejected by default, throwing during
/// argument binding and surfacing as a broken turn. <see cref="ComponentToolkitJsonCoercion"/>
/// makes the ComponentToolkit's tool argument binding tolerant of these shapes; these tests
/// invoke the REAL production AIFunctions exactly as <c>AskAIAgent.Build()</c> wires them.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class ComponentToolkitArgCoercionTests
{
    static AIFunction Tool(string name) => (AIFunction)new ComponentToolkit().AsAIFunctions(JsonSerializerOptions.Web).First(t => t.Name == name);

    static AIFunctionArguments Args(string json)
    {
        var doc = JsonDocument.Parse(json);
        var args = new AIFunctionArguments();
        foreach (var p in doc.RootElement.EnumerateObject())
            args[p.Name] = p.Value.Clone();
        return args;
    }

    static async Task<object?> Invoke(string tool, string json) => await Tool(tool).InvokeAsync(Args(json));

    [TestMethod]
    public async Task RenderChart_NumericXAxisLabels_Binds()
    {
        // "Chart the number of named Jedi deaths each year from 22 BBY to 19 BBY" —
        // the model emits the years as JSON numbers into xAxisLabels (List<string>).
        var result = await Invoke("render_chart", """{"chartType":"Bar","title":"Jedi deaths","xAxisLabels":[22,21,20,19],"series":[{"name":"Deaths","data":[2,1,3,15]}],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task RenderChart_NumericPieLabels_Binds()
    {
        var result = await Invoke("render_chart", """{"chartType":"Pie","title":"t","labels":[22,19],"series":[{"name":"Values","data":[2,15]}],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task RenderChart_StringAndNullSeriesData_Binds()
    {
        var result = await Invoke("render_chart", """{"chartType":"Bar","title":"t","xAxisLabels":["a","b","c"],"series":[{"name":"S","data":["2",null,15]}],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task RenderChart_TimeSeriesWithBceDates_Binds()
    {
        // The exact shape that failed live: the model chose TimeSeries for in-universe BBY
        // data and emitted BCE ISO dates ("-0022-...") that STJ's strict DateTime parser
        // rejects. The TolerantDateTimeConverter must let it bind (coerced to MinValue) so
        // the chart renders off the y-values + labels instead of the turn dying.
        var result = await Invoke(
            "render_chart",
            """{"chartType":"TimeSeries","title":"Named Jedi deaths","timeSeries":[{"name":"Jedi deaths","data":[{"x":"-0022-01-01T00:00:00Z","y":75},{"x":"-0019-01-01T00:00:00Z","y":228}]}],"mobileSummary":"x","references":[]}"""
        );
        Assert.IsNotNull(result);
    }

    // NOTE: the catch-all safety net for shapes the converters DON'T anticipate (e.g. an
    // object where the schema expects a scalar array) lives in ToolCallBudgetMiddleware's
    // render_* catch, not at this toolkit layer — an AIFunction-subclass wrapper does not
    // fire in the AGUI/FIC invocation path. That layer is exercised by live /ask validation.

    [TestMethod]
    public async Task RenderDataTable_NumericCell_Binds()
    {
        // Ownership table with a numeric count column — model emits a JSON number cell.
        var result = await Invoke("render_data_table", """{"title":"Owners","columns":["Artifact","Owners"],"rows":[["Sith holocron",3]],"references":[123],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task RenderDataTable_BoolAndNullCell_Binds()
    {
        var result = await Invoke("render_data_table", """{"title":"t","columns":["A","B","C"],"rows":[["Sword",true,null]],"references":[123],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task RenderDataTable_AllStringRows_StillBinds()
    {
        // Canonical clean shape must keep working.
        var result = await Invoke("render_data_table", """{"title":"t","columns":["Artifact","Owner","Year"],"rows":[["Sword","Yoda","19 BBY"]],"references":[123],"mobileSummary":"x"}""");
        Assert.IsNotNull(result);
    }
}
