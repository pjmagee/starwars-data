using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

#pragma warning disable SKEXP0001

public class ChartToolkit
{
    [KernelFunction(name: "render_chart")]
    [Description("Build a chart from the provided data")]
    public string RenderChart([Description("The chart to render")] AskChart chart)
    {
        return JsonSerializer.Serialize(chart);
    }
}
