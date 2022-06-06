using System.Globalization;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class EventTransformer
{
    public IEnumerable<TimelineEvent> Transform(Record r)
    {
        foreach (var data in r.Data)
        {
            foreach (var link in data.Links.Where(IsValid))
            {
                yield return new TimelineEvent()
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

    private static bool IsValid(HyperLink link)
    {
        if (string.IsNullOrWhiteSpace(link.Content))
            return false;
        
        var containsYear =  char.IsDigit(link.Content.First());
        var containsDemarcation = link.Content.Contains("BBY") || link.Content.Contains("ABY");
        var linkContainsDemarcation = link.Href.Contains("_BBY") || link.Href.Contains("_ABY"); 
        
        return containsYear && containsDemarcation && linkContainsDemarcation;
    }
}