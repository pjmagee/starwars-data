using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Helpers;

public class YearComparer : Comparer<string>
{
    public const string BBY = nameof(BBY);
    public const string ABY = nameof(ABY);
    
    public override int Compare(string? x, string? y)
    {
        if(x is null || y is null) return 0;
        
        if (x.Contains(BBY) && y.Contains(ABY)) return -1;
        if (x.Contains(ABY) && y.Contains(BBY)) return 1;

        var xDemarcation = x.Contains(BBY) ? Demarcation.Bby : Demarcation.Aby;
        var yDemarcation = y.Contains(BBY) ? Demarcation.Bby : Demarcation.Aby;
        
        var xYear = double.Parse(x.Split(' ')[0]);
        var yYear = double.Parse(y.Split(' ')[0]);

        return xDemarcation switch
        {
            Demarcation.Bby when yDemarcation == Demarcation.Bby => yYear.CompareTo(xYear),
            Demarcation.Bby when yDemarcation == Demarcation.Aby => yYear.CompareTo(xYear),
            _ => xYear.CompareTo(yYear)
        };
    }
}