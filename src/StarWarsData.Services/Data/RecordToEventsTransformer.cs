using System.Globalization;
using StarWarsData.Models.Mongo;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Helpers;

namespace StarWarsData.Services.Data;

public class RecordToEventsTransformer
{
    private readonly YearHelper _yearHelper;

    public RecordToEventsTransformer(YearHelper yearHelper)
    {
        _yearHelper = yearHelper;
    }
    
    public IEnumerable<TimelineEvent> Transform(Record r)
    {
        foreach (var data in r.Data)
        {
            foreach (var link in data.Links.Where(_yearHelper.IsValidLink))
            {
                yield return new TimelineEvent
                {
                    Title = r.PageTitle,
                    DateEvent = GetDateEvent(data),
                    Demarcation = link.Content.Contains("ABY") ? Demarcation.ABY : link.Content.Contains("BBY") ? Demarcation.BBY : Demarcation.Unset,
                    Year = ParseYear(link.Content),
                    Template = r.Template,
                    ImageUrl = r.ImageUrl,
                    Properties = r.Data,
                };
            }
        }
    }

    private string? GetDateEvent(InfoboxProperty data) => data.Label switch { "Date" => String.Empty, _ => data.Label };

    private double ParseYear(string text) => double.Parse(new String(text.Split(' ', '-')[0].Where(c => !char.IsLetter(c)).ToArray()), NumberStyles.Any);
}