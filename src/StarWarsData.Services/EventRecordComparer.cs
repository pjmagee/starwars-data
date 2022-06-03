using StarWarsData.Models;

namespace StarWarsData.Services;

public class EventRecordComparer : IComparer<Record>
{
    // Compare should be based on the Template Structure
    // If it's an Event: "Date"
    // If it's a Treaty: "Date established"
    // If it's a Law: "Date"
    // If it's a Campaign: "Begin" and "End"
    public int Compare(Record? x, Record? y)
    {
        // We expect the first Data[0].Label = Values
        // Of which, there is probably one item in the array (Year BBY/ABY)

        string GetDateValue(Record r)
        {
            return r.Data.Find(d => d.Label == "Date").Links
                .Find(l => l.Content.Contains("BBY") || l.Content.Contains("ABY"))
                .Content;
        }
        
        var xValue = GetDateValue(x);
        var yValue = GetDateValue(y);

        if (xValue.Contains("BBY") && yValue.Contains("ABY"))
            return -1; // x is less than y
        
        if (xValue.Contains("ABY") && yValue.Contains("BBY"))
            return 1; // x is greater than y
        
        // long.Parse(" 13,000,000,000  ", System.Globalization.NumberStyles.Number)
        // 19.1, etc (We need to handle floating point numbers...)
        
        var xYear = double.Parse(xValue.Split(" ")[0], System.Globalization.NumberStyles.Number);
        var yYear = double.Parse(yValue.Split(" ")[0], System.Globalization.NumberStyles.Number);

        // Swap Compare because a higher number is < 0 ABY

        if (xValue.Contains("BBY") && yValue.Contains("BBY"))
            return yYear.CompareTo(xYear);
        
        if(xValue.Contains("BBY") && yValue.Contains("ABY"))
            return yYear.CompareTo(xYear);

        return xYear.CompareTo(yYear);
    }
}