using System.Globalization;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class InfoboxToEventsTransformer
{
    readonly YearHelper _yearHelper;
    readonly ILogger<InfoboxToEventsTransformer> _logger;
    readonly TemplateHelper _templateHelper;

    public InfoboxToEventsTransformer(
        ILogger<InfoboxToEventsTransformer> logger,
        TemplateHelper templateHelper,
        YearHelper yearHelper
    )
    {
        _yearHelper = yearHelper;
        _logger = logger;
        _templateHelper = templateHelper;
    }

    public IEnumerable<TimelineEvent> Transform(Page page)
    {
        if (page.Infobox == null)
            yield break;

        var template = page.Infobox.Template ?? "Unknown";
        var title = page.Title;
        var imageUrl = page.Infobox.ImageUrl;
        var data = page.Infobox.Data;

        foreach (var prop in data)
        {
            foreach (var link in prop.Links.Where(_yearHelper.IsValidLink))
            {
                var year = ParseYear(link.Content);

                if (year == null)
                {
                    _logger.LogWarning(
                        "Could not parse year from link content '{LinkContent}' for page '{PageTitle}'. Skipping this event",
                        link.Content,
                        title
                    );
                    continue;
                }

                yield return new TimelineEvent
                {
                    Title = title,
                    DateEvent = GetDateEvent(prop),
                    Demarcation = GetDemarcation(link),
                    Year = year.Value,
                    Template = template,
                    ImageUrl = imageUrl,
                    Properties = data,
                    Continuity = page.Continuity,
                    Universe = page.Universe,
                    PageId = page.PageId,
                    WikiUrl = page.WikiUrl,
                };
            }
        }
    }

    static Demarcation GetDemarcation(HyperLink link) =>
        link.Content.Contains("ABY") ? Demarcation.Aby
        : link.Content.Contains("BBY") ? Demarcation.Bby
        : Demarcation.Unset;

    string? GetDateEvent(InfoboxProperty data) =>
        data.Label switch
        {
            "Date" => String.Empty,
            _ => data.Label,
        };

    float? ParseYear(string text)
    {
        var yearString = new String(
            text.Split(' ', '-')[0].Where(c => !char.IsLetter(c)).ToArray()
        );
        if (
            float.TryParse(
                yearString,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out float result
            )
        )
        {
            return result;
        }
        _logger.LogWarning("Failed to parse year from text: {Text}", text);
        return null;
    }
}
