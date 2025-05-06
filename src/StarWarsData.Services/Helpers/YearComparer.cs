using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Helpers;

public class YearComparer : Comparer<string>
{
    public const string Bby = "BBY";
    public const string Aby = "ABY";
    
    public override int Compare(string? x, string? y)
    {
        if(x is null || y is null) return 0;
        
        if (x.Contains(Bby, StringComparison.OrdinalIgnoreCase) && y.Contains(Aby, StringComparison.OrdinalIgnoreCase)) return -1;
        if (x.Contains(Aby, StringComparison.OrdinalIgnoreCase) && y.Contains(Bby, StringComparison.OrdinalIgnoreCase)) return 1;

        var xDemarcation = x.Contains(Bby, StringComparison.OrdinalIgnoreCase) ? Demarcation.Bby : Demarcation.Aby;
        var yDemarcation = y.Contains(Bby, StringComparison.OrdinalIgnoreCase) ? Demarcation.Bby : Demarcation.Aby;

        // Extract the first number (handles ranges like "20.9-20.8 BBY")
        var xYearStr = x.Split(' ')[0].Split('-')[0];
        var yYearStr = y.Split(' ')[0].Split('-')[0];
        
        if (!double.TryParse(xYearStr, out var xYear) || !double.TryParse(yYearStr, out var yYear))
            return 0; // fallback if parsing fails

        return xDemarcation switch
        {
            Demarcation.Bby => yYear.CompareTo(xYear),
            _ => xYear.CompareTo(yYear)
        };
    }
}

