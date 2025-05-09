namespace StarWarsData.Models.Queries;

public class GalaxyMapItem
{
    public int Id { get; set; }
    public string Letter { get; set; } = null!;
    public int Number { get; set; }
    public string Name { get; set; } = null!;
}

// DTOs for Galaxy API
public class SectorDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public class RegionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public class SystemDetailsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? GridSquare { get; set; }
    public List<string> Planets { get; set; } = new List<string>();
    public List<string> Neighbors { get; set; } = new List<string>();
}

public class CelestialBodyDetailsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Class { get; set; } = null!;
    public string? GridSquare { get; set; }
    public string? Sector { get; set; }
    public string? Region { get; set; }
    // all other infobox properties
    public Dictionary<string, List<string>> AdditionalData { get; set; } = new();
}

// new DTOs for hierarchy
public class SystemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public class PlanetDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}