namespace StarWarsData.Models.Queries;

public class Era
{
    public string Name { get; set; } = string.Empty;
    public float StartYear { get; set; }
    public Demarcation StartDemarcation { get; set; }
    public float EndYear { get; set; }
    public Demarcation EndDemarcation { get; set; }
    public string Description { get; set; } = string.Empty;
}
