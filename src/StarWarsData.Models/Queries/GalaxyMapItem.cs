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
    public List<string> Planets { get; set; } = [];
    public List<string> Neighbors { get; set; } = [];
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
    public Dictionary<string, List<string>> AdditionalData { get; set; } = [];
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

public class NebulaDetailsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? GridSquare { get; set; }
    public string? Sector { get; set; }
    public string? Region { get; set; }
    public Dictionary<string, List<string>> AdditionalData { get; set; } = [];
}

public class MapSearchResult
{
    public string GridKey { get; set; } = null!;
    public int PageId { get; set; }
    public string MatchedName { get; set; } = null!;
    public string? Template { get; set; }
    public string MatchType { get; set; } = null!; // "direct", "linked", "semantic", "semantic-linked"
    public string? LinkedVia { get; set; } // e.g. "Homeworld of Luke Skywalker"
    public int? SourcePageId { get; set; } // For linked results: the entity that referenced the location
    public string? SourceName { get; set; } // For linked results: name of the referencing entity
    public string? Snippet { get; set; } // Semantic search: text excerpt
    public double? Score { get; set; } // Semantic search: relevance score
}
