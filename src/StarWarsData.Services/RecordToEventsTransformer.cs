using System.Globalization;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
// Added for logging
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

public class RecordToEventsTransformer
{
    readonly YearHelper _yearHelper;
    readonly ILogger<RecordToEventsTransformer> _logger; // Added logger
    readonly TemplateHelper _templateHelper; // Added TemplateHelper

    public RecordToEventsTransformer(
        ILogger<RecordToEventsTransformer> logger,
        TemplateHelper templateHelper,
        YearHelper yearHelper)
    {
        _yearHelper = yearHelper;
        _logger = logger;
        _templateHelper = templateHelper;
    }
    
    // Change the return type to IEnumerable<TimelineEventDocument>
    public IEnumerable<TimelineEvent> Transform(InfoboxRecord r)
    {
        foreach (var data in r.Data)
        {
            foreach (var link in data.Links.Where(_yearHelper.IsValidLink))
            {
                var year = ParseYear(link.Content); // Get nullable year

                // Skip if year parsing failed
                if (year == null)
                {
                    // Removed trailing period from log message again
                    _logger.LogWarning("Could not parse year from link content '{LinkContent}' for page '{PageTitle}'. Skipping this event", link.Content, r.PageTitle);
                    continue;
                }

                yield return new TimelineEvent
                {
                    Title = r.PageTitle,
                    DateEvent = GetDateEvent(data),
                    Demarcation = GetDemarcation(link),
                    Year = year.Value,
                    Template = r.TemplateUrl,
                    CleanedTemplate = _templateHelper.CleanTemplate(r.TemplateUrl),
                    ImageUrl = r.ImageUrl,
                    Properties = r.Data
                };
            }
        }
    }

    static Demarcation GetDemarcation(HyperLink link) => link.Content.Contains("ABY") ? Demarcation.Aby : link.Content.Contains("BBY") ? Demarcation.Bby : Demarcation.Unset;

    string? GetDateEvent(InfoboxProperty data) => data.Label switch
    {
        "Date" => String.Empty, _ => data.Label
    };

    // Updated to return float? and use TryParse
    float? ParseYear(string text)
    {
        var yearString = new String(text.Split(' ', '-')[0].Where(c => !char.IsLetter(c)).ToArray());
        // Use InvariantCulture for consistent parsing regardless of system locale
        if (float.TryParse(yearString, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
        {
            return result;
        }
        _logger.LogWarning("Failed to parse year from text: {Text}", text); // Added logging for parse failure
        return null; // Return null if parsing fails
    }
}
