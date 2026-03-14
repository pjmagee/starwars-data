using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class ChartToolkit
{
    public AskChart? Result { get; private set; }

    [Description("Build a chart from the provided data")]
    public string RenderChart([Description("The chart to render")] AskChart chart)
    {
        Result = chart;
        return JsonSerializer.Serialize(chart);
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(RenderChart, "render_chart");
}
