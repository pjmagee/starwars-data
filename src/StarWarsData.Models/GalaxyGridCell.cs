namespace StarWarsData.Models;

public class GalaxyGridCell
{
    public string Letter { get; set; } = null!;
    public int Number { get; set; }
    public string? Sector { get; set; }
    public List<SystemWithPlanets> Systems { get; set; } = new();
    public List<GalaxyMapItem> PlanetsWithoutSystem { get; set; } = new();
}

public class SystemWithPlanets
{
    public string Name { get; set; } = null!;
    public List<GalaxyMapItem> Planets { get; set; } = new();
}
