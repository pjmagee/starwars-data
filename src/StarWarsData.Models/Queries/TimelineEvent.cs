using StarWarsData.Models.Mongo;

namespace StarWarsData.Models.Queries;

public class TimelineEvent : IComparable<TimelineEvent>
{
    public string DisplayTitle => $"{DateEvent} {Title} ({Template})".Trim();
    
    public string DisplayYear => $"{Year:##,###} {Demarcation}";
    public string? DisplayImageWithoutRevision => ImageUrl?.Split(new [] {"/revision" }, StringSplitOptions.RemoveEmptyEntries)[0];
    public string Title { get; set; }
    public string Template { get; set; }
    public string? ImageUrl { get; set; }
    public Demarcation Demarcation { get; set; }
    public double Year { get; set; }
    public List<InfoboxProperty> Properties { get; set; }
    public string? DateEvent { get; set; }
    
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