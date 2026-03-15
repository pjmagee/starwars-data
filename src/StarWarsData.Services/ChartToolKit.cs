using System.ComponentModel;
using Microsoft.Extensions.AI;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class ChartToolkit
{
    public AskChart? Result { get; private set; }

    [Description("Render a chart or family tree. Call this exactly once as your final action.")]
    public AskChart RenderChart(
        [Description("Bar, Line, Pie, Donut, StackedBar, TimeSeries, or FamilyTree")]
            string chartType,
        [Description("Descriptive title for the chart")] string title,
        [Description("For Bar/Line/StackedBar: category labels for the X-axis")]
            List<string>? xAxisLabels = null,
        [Description("For Pie/Donut: slice labels")] List<string>? labels = null,
        [Description("For Bar/Line/StackedBar/Pie/Donut: array of { name, data: number[] }")]
            List<AskChartSeries>? series = null,
        [Description(
            "For TimeSeries: array of { name, data: [{ x: ISO-date-string, y: number }] }"
        )]
            List<AskTimeSeriesChartSeries>? timeSeries = null,
        [Description("For FamilyTree: the integer _id (PageId) of the character from MongoDB")]
            int? familyTreeCharacterId = null,
        [Description("For FamilyTree: the PageTitle of the character from MongoDB")]
            string? familyTreeCharacterName = null
    )
    {
        if (!Enum.TryParse<AskChartType>(chartType, ignoreCase: true, out var parsedType))
            parsedType = AskChartType.Bar;

        Result = new AskChart
        {
            AskChartType = parsedType,
            Title = title,
            XAxisLabels = xAxisLabels,
            Labels = labels,
            Series = series,
            TimeSeries = timeSeries,
            FamilyTreeCharacterId = familyTreeCharacterId,
            FamilyTreeCharacterName = familyTreeCharacterName,
        };
        return Result;
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(RenderChart, "render_chart");
}
