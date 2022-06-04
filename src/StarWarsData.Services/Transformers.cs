using System.Globalization;
using StarWarsData.Models;

namespace StarWarsData.Services;

public class EventTransformer : RecordTransformer
{
    private string[] StartTerms = { "Beginning", "Start", "Begin" };
    
    private string[] EndTerms = { "End", "Ended" };

    private string[] OnceTerms = { "Date established", "Date" };

    public override IEnumerable<TimelineEvent> Transform(Record r)
    {
        foreach (var data in r.Data)
        {
            foreach (var link in data.Links.Where(IsValid))
            {
                yield return new TimelineEvent()
                {
                    Title = r.PageTitle,
                    Span = OnceTerms.Contains(data.Label) ? EventSpan.Once : StartTerms.Contains(data.Label) ? EventSpan.Start : EndTerms.Contains(data.Label) ? EventSpan.End : EventSpan.Unknown,
                    Demarcation = link.Content.Contains("ABY") ? Demarcation.ABY : link.Content.Contains("BBY") ? Demarcation.BBY : Demarcation.Unset,
                    Year = ParseYear(link.Content),
                    Template = r.Template,
                    Values = r.Data.SelectMany(r => r.Values).ToList()
                };
            }
        }
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