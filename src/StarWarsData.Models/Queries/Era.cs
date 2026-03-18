namespace StarWarsData.Models.Queries;

public class Era
{
    public string Name { get; set; } = string.Empty;
    public float StartYear { get; set; }
    public Demarcation StartDemarcation { get; set; }
    public float EndYear { get; set; }
    public Demarcation EndDemarcation { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Well-known Star Wars Canon eras with their approximate year ranges.
    /// </summary>
    public static readonly Era[] CanonEras =
    [
        new()
        {
            Name = "Dawn of the Jedi",
            StartYear = 25000,
            StartDemarcation = Demarcation.Bby,
            EndYear = 25000,
            EndDemarcation = Demarcation.Bby,
            Description = "The earliest days of the Force and the Jedi",
        },
        new()
        {
            Name = "Old Republic",
            StartYear = 25000,
            StartDemarcation = Demarcation.Bby,
            EndYear = 1032,
            EndDemarcation = Demarcation.Bby,
            Description = "The era of the ancient Republic and Sith wars",
        },
        new()
        {
            Name = "High Republic",
            StartYear = 500,
            StartDemarcation = Demarcation.Bby,
            EndYear = 100,
            EndDemarcation = Demarcation.Bby,
            Description = "The golden age of the Jedi and the Republic",
        },
        new()
        {
            Name = "Fall of the Jedi",
            StartYear = 100,
            StartDemarcation = Demarcation.Bby,
            EndYear = 19,
            EndDemarcation = Demarcation.Bby,
            Description = "The decline of the Republic and the Clone Wars",
        },
        new()
        {
            Name = "Reign of the Empire",
            StartYear = 19,
            StartDemarcation = Demarcation.Bby,
            EndYear = 0,
            EndDemarcation = Demarcation.Bby,
            Description = "The dark times under Imperial rule",
        },
        new()
        {
            Name = "Age of Rebellion",
            StartYear = 0,
            StartDemarcation = Demarcation.Bby,
            EndYear = 5,
            EndDemarcation = Demarcation.Aby,
            Description = "The Galactic Civil War and the fall of the Empire",
        },
        new()
        {
            Name = "New Republic",
            StartYear = 5,
            StartDemarcation = Demarcation.Aby,
            EndYear = 34,
            EndDemarcation = Demarcation.Aby,
            Description = "The rise of the New Republic after Endor",
        },
        new()
        {
            Name = "Rise of the First Order",
            StartYear = 34,
            StartDemarcation = Demarcation.Aby,
            EndYear = 35,
            EndDemarcation = Demarcation.Aby,
            Description = "The conflict with the First Order",
        },
        new()
        {
            Name = "New Jedi Order",
            StartYear = 35,
            StartDemarcation = Demarcation.Aby,
            EndYear = 100,
            EndDemarcation = Demarcation.Aby,
            Description = "The rebuilding of the Jedi Order",
        },
    ];
}
