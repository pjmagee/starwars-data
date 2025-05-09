namespace StarWarsData.Models.Queries;

public class GalaxyGridCell
{
    public string Letter { get; set; } = null!;
    public int Number { get; set; }
    public string? Sector { get; set; }
    public int? SectorId { get; set; }
    public List<SystemWithPlanets> Systems { get; set; } = new();
    public List<GalaxyMapItem> PlanetsWithoutSystem { get; set; } = new();
    // Region information
    public string? Region { get; set; }
    public int? RegionId { get; set; }
}

public class SystemWithPlanets
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public List<GalaxyMapItem> Planets { get; set; } = new();
}
