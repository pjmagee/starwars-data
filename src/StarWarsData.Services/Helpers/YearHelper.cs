using MongoDB.Bson;
using StarWarsData.Models.Mongo;

namespace StarWarsData.Services.Helpers;

public class YearHelper(YearComparer yearComparer)
{
    public YearComparer YearComparer { get; } = yearComparer;

    public bool IsValidLink(HyperLink hyperLink)
    {
        if (string.IsNullOrWhiteSpace(hyperLink.Content) || string.IsNullOrWhiteSpace(hyperLink.Href)) return false;
        var containsYear =  char.IsDigit(hyperLink.Content.First());
        var containsDemarcation = hyperLink.Content.Contains(YearComparer.BBY, StringComparison.OrdinalIgnoreCase) || hyperLink.Content.Contains(YearComparer.ABY, StringComparison.OrdinalIgnoreCase);
        var linkContainsDemarcation = containsDemarcation && (hyperLink.Href.Contains("_BBY") || hyperLink.Href.Contains("_ABY"));
        return containsYear && containsDemarcation && linkContainsDemarcation;
    }
    
    public bool IsValidLink(BsonValue link) => !link.IsBsonNull && IsValidLink(new HyperLink() { Content = link["Content"].AsString, Href = link["Href"].AsString });
}