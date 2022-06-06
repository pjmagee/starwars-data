using StarWarsData.Models;

namespace StarWarsData.Services;

public class YearComparer : Comparer<string>
{
    public override int Compare(string x, string y)
    {
        if (x.Contains("BBY") && y.Contains("ABY")) return -1;
        if (x.Contains("ABY") && y.Contains("BBY")) return 1;

        var xDemarcation = x.Contains("BBY") ? Demarcation.BBY : Demarcation.ABY;
        var yDemarcation = y.Contains("BBY") ? Demarcation.BBY : Demarcation.ABY;
        
        var xYear = float.Parse(x.Split(' ')[0]);
        var yYear = float.Parse(y.Split(' ')[0]);

        return xDemarcation switch
        {
            Demarcation.BBY when yDemarcation == Demarcation.BBY => yYear.CompareTo(xYear),
            Demarcation.BBY when yDemarcation == Demarcation.ABY => yYear.CompareTo(xYear),
            _ => xYear.CompareTo(yYear)
        };
    }
}