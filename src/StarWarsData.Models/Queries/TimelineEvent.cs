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
        if (other is null) return 1;
        
        switch (Demarcation)
        {
            case Demarcation.Bby when other.Demarcation == Demarcation.Aby: return -1;
            case Demarcation.Aby when other.Demarcation == Demarcation.Bby: return 1;
        }

        return Demarcation switch
        {
            Demarcation.Bby when other.Demarcation == Demarcation.Bby => other.Year.CompareTo(Year),
            Demarcation.Bby when other.Demarcation == Demarcation.Aby => other.Year.CompareTo(Year),
            _ => Year.CompareTo(other.Year)
        };
    }
}