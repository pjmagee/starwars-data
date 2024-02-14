using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Helpers;

public class YearComparer : Comparer<string>
{
    public const string BBY = nameof(BBY);
    public const string ABY = nameof(ABY);
    
    public override int Compare(string x, string y)
    {
        if (x.Contains(BBY) && y.Contains(ABY)) return -1;
        if (x.Contains(ABY) && y.Contains(BBY)) return 1;

        var xDemarcation = x.Contains(BBY) ? Demarcation.BBY : Demarcation.ABY;
        var yDemarcation = y.Contains(BBY) ? Demarcation.BBY : Demarcation.ABY;
        
        var xYear = double.Parse(x.Split(' ')[0]);
        var yYear = double.Parse(y.Split(' ')[0]);

        return xDemarcation switch
        {
            Demarcation.BBY when yDemarcation == Demarcation.BBY => yYear.CompareTo(xYear),
            Demarcation.BBY when yDemarcation == Demarcation.ABY => yYear.CompareTo(xYear),
            _ => xYear.CompareTo(yYear)
        };
    }
}