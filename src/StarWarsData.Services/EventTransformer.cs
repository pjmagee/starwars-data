using System.Globalization;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class EventTransformer : RecordTransformer
{
    public override IEnumerable<TimelineEvent> Transform(Record r)
    {
        foreach (var data in r.Data)
        {
            foreach (var link in data.Links.Where(IsValid))
            {
                yield return new TimelineEvent()
                {
                    Title = r.PageTitle,
                    EventType = GetEventType(data),
                    Demarcation = link.Content.Contains("ABY") ? Demarcation.ABY : link.Content.Contains("BBY") ? Demarcation.BBY : Demarcation.Unset,
                    Year = ParseYear(link.Content),
                    Template = r.Template,
                    Values = r.Data.Where(x => !x.Label!.Equals("Titles")).SelectMany(d => d.Values).Distinct().ToList()
                };
            }
        }
    }

    private string? GetEventType(InfoboxProperty data)
    {
        return data.Label switch
        {
            "Date" => String.Empty,
            _ => data.Label
        };
    }

    private double ParseYear(string text)
    {
        return double.Parse(new String(text.Split(' ', '-')[0].Where(c => !char.IsLetter(c)).ToArray()), NumberStyles.Any);
    }

    private static bool IsValid(HyperLink link)
    {
        var containsYear =  char.IsDigit(link.Content[0]);
        var containsDemarcation = link.Content.Contains("BBY") || link.Content.Contains("ABY");
        var linkContainsDemarcation = link.Href.Contains("_BBY") || link.Href.Contains("_ABY"); 
        
        return containsYear && containsDemarcation && linkContainsDemarcation;
    }
}