namespace StarWarsData.Models;

public enum Demarcation
{
    Unset,
    ABY,
    BBY
}

public class TimelineEvent : IComparable<TimelineEvent>
{
    public string Title { get; set; }
    public string Template { get; set; }
   
    public Demarcation Demarcation { get; set; }
    public double Year { get; set; }
    
    public List<string> Values { get; set; }
    public EventSpan Span { get; set; }
    
    public int CompareTo(TimelineEvent? other)
    {
        switch (Demarcation)
        {
            case Demarcation.BBY when other.Demarcation == Demarcation.ABY: return -1;
            case Demarcation.ABY when other.Demarcation == Demarcation.BBY: return 1;
        }

        return Demarcation switch
        {
            Demarcation.BBY when other.Demarcation == Demarcation.BBY => other.Year.CompareTo(Year),
            Demarcation.BBY when other.Demarcation == Demarcation.ABY => other.Year.CompareTo(Year),
            _ => Year.CompareTo(other.Year)
        };
    }
}