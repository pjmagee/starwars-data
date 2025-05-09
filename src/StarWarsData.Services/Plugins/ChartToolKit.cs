using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Plugins;

#pragma warning disable SKEXP0001

public class ChartToolkit
{
    [KernelFunction(name: "render_chart")]
    [Description("Build a chart from the provided data")]
    public string RenderChart(
        [Description("The chart to render")]
        AskChart chart)
    {
        return JsonSerializer.Serialize(chart);
    }
}