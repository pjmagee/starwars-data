using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services.Data;

public class MapService
{
    private readonly ILogger<MapService> _logger;
    private readonly Settings _settings;
    private readonly IMongoDatabase _db;
    
    public MapService(ILogger<MapService> logger, IMongoDatabase db, Settings settings)
    {
        _logger = logger;
        _settings = settings;
        _db = db;
    }

    public async Task<IEnumerable<GalaxyMapItem>> GetPlanets()
    {
        var planets = new List<GalaxyMapItem>();
        // Query only the CelestialBody collection for grid square info
        var col = _db.GetCollection<Record>("CelestialBody");
        // Only include grid-enabled bodies that are terrestrial planets
        var gridFilter = Builders<Record>.Filter.ElemMatch(r => r.Data, d => d.Label == "Grid square");
        var classFilter = Builders<Record>.Filter.ElemMatch(r => r.Data,
            d => d.Label == "Class" && d.Values.Contains("Terrestrial"));
        var filter = Builders<Record>.Filter.And(gridFilter, classFilter);
        var recs = await col.Find(filter).ToListAsync();
        foreach (var rec in recs)
        {
            var prop = rec.Data.FirstOrDefault(d => d.Label == "Grid square");
            if (prop?.Values == null || prop.Values.Count == 0) continue;
            var loc = prop.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2) continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number)) continue;
            planets.Add(new GalaxyMapItem { Id = rec.PageId, Letter = letter, Number = number, Name = rec.PageTitle });
        }
        return planets;
    }

    public async Task<IEnumerable<GalaxyGridCell>> GetGalaxyGridCells()
    {
        var col = _db.GetCollection<Record>("CelestialBody");
        // preload system, sector, and region name->Id maps
        var sysCol = _db.GetCollection<Record>("System");
        var sysRecs = await sysCol.Find(_ => true).ToListAsync();
        var systemMap = sysRecs.ToDictionary(r => r.PageTitle, r => r.PageId);
        var sectorCol = _db.GetCollection<Record>("Sector");
        var sectorRecs = await sectorCol.Find(_ => true).ToListAsync();
        var sectorMap = sectorRecs.ToDictionary(r => r.PageTitle, r => r.PageId);
        var regionCol = _db.GetCollection<Record>("Region");
        var regionRecs = await regionCol.Find(_ => true).ToListAsync();
        var regionMap = regionRecs.ToDictionary(r => r.PageTitle, r => r.PageId);
        var gridFilter = Builders<Record>.Filter.ElemMatch(r => r.Data, d => d.Label == "Grid square");
        var classFilter = Builders<Record>.Filter.ElemMatch(r => r.Data, d => d.Label == "Class" && d.Values.Contains("Terrestrial"));
        var filter = Builders<Record>.Filter.And(gridFilter, classFilter);
        var recs = await col.Find(filter).ToListAsync();

        // Optionally, get sector info from Region/Sector fields
        var grid = new Dictionary<string, GalaxyGridCell>();
        foreach (var rec in recs)
        {
            var gridProp = rec.Data.FirstOrDefault(d => d.Label == "Grid square");
            if (gridProp?.Values == null || gridProp.Values.Count == 0) continue;
            var loc = gridProp.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2) continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number)) continue;
            var key = $"{letter}-{number}";

            var sectorName = rec.Data.FirstOrDefault(d => d.Label == "Sector")?.Values.FirstOrDefault();
            var regionName = rec.Data.FirstOrDefault(d => d.Label == "Region")?.Values.FirstOrDefault();
            var system = rec.Data.FirstOrDefault(d => d.Label == "System")?.Values.FirstOrDefault();
            var planet = new GalaxyMapItem { Id = rec.PageId, Letter = letter, Number = number, Name = rec.PageTitle };

            if (!grid.TryGetValue(key, out var cell))
            {
                cell = new GalaxyGridCell
                {
                    Letter = letter,
                    Number = number,
                    Sector = sectorName,
                    SectorId = sectorName != null && sectorMap.TryGetValue(sectorName, out var sid) ? sid : null,
                    Region = regionName,
                    RegionId = regionName != null && regionMap.TryGetValue(regionName, out var rid) ? rid : null
                };
                grid[key] = cell;
            }

            if (!string.IsNullOrWhiteSpace(system))
            {
                var sys = cell.Systems.FirstOrDefault(s => s.Name == system);
                if (sys == null)
                {
                    // map system name to ID
                    var sysId = systemMap.TryGetValue(system, out var sid) ? sid : 0;
                    sys = new SystemWithPlanets { Id = sysId, Name = system };
                    cell.Systems.Add(sys);
                }
                sys.Planets.Add(planet);
            }
            else
            {
                cell.PlanetsWithoutSystem.Add(planet);
            }
        }
        return grid.Values;
    }

    public async Task<IEnumerable<SectorDto>> GetSectorsAsync()
    {
        var col = _db.GetCollection<Record>("Sector");
        var recs = await col.Find(_ => true).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.PageTitle });
    }

    public async Task<SectorDto?> GetSectorAsync(int id)
    {
        var col = _db.GetCollection<Record>("Sector");
        var rec = await col.Find(r => r.PageId == id).FirstOrDefaultAsync();
        if (rec == null) return null;
        return new SectorDto { Id = rec.PageId, Name = rec.PageTitle };
    }

    public async Task<IEnumerable<RegionDto>> GetRegionsAsync()
    {
        var col = _db.GetCollection<Record>("Region");
        var recs = await col.Find(_ => true).ToListAsync();
        return recs.Select(r => new RegionDto { Id = r.PageId, Name = r.PageTitle });
    }

    public async Task<RegionDto?> GetRegionAsync(int id)
    {
        var col = _db.GetCollection<Record>("Region");
        var rec = await col.Find(r => r.PageId == id).FirstOrDefaultAsync();
        if (rec == null) return null;
        return new RegionDto { Id = rec.PageId, Name = rec.PageTitle };
    }

    public async Task<SystemDetailsDto?> GetSystemDetailsAsync(int id)
    {
        var col = _db.GetCollection<Record>("System");
        var rec = await col.Find(r => r.PageId == id).FirstOrDefaultAsync();
        if (rec == null) return null;
        var grid = rec.Data.FirstOrDefault(d => d.Label == "Grid square")?.Values.FirstOrDefault();
        var planets = rec.Data.FirstOrDefault(d => d.Label == "Planets")?.Values ?? new List<string>();
        var neighbors = rec.Data.FirstOrDefault(d => d.Label == "Neighboring systems")?.Values ?? new List<string>();
        return new SystemDetailsDto
        {
            Id = rec.PageId,
            Name = rec.PageTitle,
            GridSquare = grid,
            Planets = planets.ToList(),
            Neighbors = neighbors.ToList()
        };
    }

    public async Task<CelestialBodyDetailsDto?> GetCelestialBodyDetailsAsync(int id)
    {
        var col = _db.GetCollection<Record>("CelestialBody");
        var rec = await col.Find(r => r.PageId == id).FirstOrDefaultAsync();
        if (rec == null) return null;
        var cls = rec.Data.FirstOrDefault(d => d.Label == "Class")?.Values.FirstOrDefault() ?? string.Empty;
        var grid = rec.Data.FirstOrDefault(d => d.Label == "Grid square")?.Values.FirstOrDefault();
        var sector = rec.Data.FirstOrDefault(d => d.Label == "Sector")?.Values.FirstOrDefault();
        var region = rec.Data.FirstOrDefault(d => d.Label == "Region")?.Values.FirstOrDefault();
        var additional = rec.Data
            .Where(d => d.Label is not null && !new[] { "Class", "Grid square", "Sector", "Region" }.Contains(d.Label))
            .ToDictionary(d => d.Label!, d => d.Values);
        return new CelestialBodyDetailsDto
        {
            Id = rec.PageId,
            Name = rec.PageTitle,
            Class = cls,
            GridSquare = grid,
            Sector = sector,
            Region = region,
            AdditionalData = additional
        };
    }

    // Get sectors by region
    public async Task<IEnumerable<SectorDto>> GetSectorsByRegionAsync(int regionId)
    {
        var regionCol = _db.GetCollection<Record>("Region");
        var region = await regionCol.Find(r => r.PageId == regionId).FirstOrDefaultAsync();
        if (region == null) return Enumerable.Empty<SectorDto>();
        var regionName = region.PageTitle;
        var col = _db.GetCollection<Record>("Sector");
        var recs = await col.Find(r => r.Data.Any(d => d.Label == "Region(s)" && d.Values.Contains(regionName))).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.PageTitle });
    }

    // Get systems by sector
    public async Task<IEnumerable<SystemDto>> GetSystemsBySectorAsync(int sectorId)
    {
        var sectorCol = _db.GetCollection<Record>("Sector");
        var sector = await sectorCol.Find(s => s.PageId == sectorId).FirstOrDefaultAsync();
        if (sector == null) return Enumerable.Empty<SystemDto>();
        var sectorName = sector.PageTitle;
        var col = _db.GetCollection<Record>("System");
        var recs = await col.Find(r => r.Data.Any(d => d.Label == "Sector" && d.Values.Contains(sectorName))).ToListAsync();
        return recs.Select(r => new SystemDto { Id = r.PageId, Name = r.PageTitle });
    }

    // Get planets by system
    public async Task<IEnumerable<PlanetDto>> GetPlanetsBySystemAsync(int systemId)
    {
        var sysCol = _db.GetCollection<Record>("System");
        var system = await sysCol.Find(s => s.PageId == systemId).FirstOrDefaultAsync();
        if (system == null) return Enumerable.Empty<PlanetDto>();
        var systemName = system.PageTitle;
        var col = _db.GetCollection<Record>("CelestialBody");
        var recs = await col.Find(r => r.Data.Any(d => d.Label == "System" && d.Values.Contains(systemName))).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.PageTitle });
    }

    // Get orphan planets in region (not in any system)
    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInRegionAsync(int regionId)
    {
        var regionCol = _db.GetCollection<Record>("Region");
        var region = await regionCol.Find(r => r.PageId == regionId).FirstOrDefaultAsync();
        if (region == null) return Enumerable.Empty<PlanetDto>();
        var regionName = region.PageTitle;
        var col = _db.GetCollection<Record>("CelestialBody");
        var recs = await col.Find(r => r.Data.Any(d => d.Label == "Region" && d.Values.Contains(regionName))
                                 && r.Data.All(d => d.Label != "System" || d.Values.Count == 0)
                        ).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.PageTitle });
    }

    // Get orphan planets in sector (not in any system)
    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInSectorAsync(int sectorId)
    {
        var sectorCol = _db.GetCollection<Record>("Sector");
        var sector = await sectorCol.Find(s => s.PageId == sectorId).FirstOrDefaultAsync();
        if (sector == null) return Enumerable.Empty<PlanetDto>();
        var sectorName = sector.PageTitle;
        var col = _db.GetCollection<Record>("CelestialBody");
        var recs = await col.Find(r => r.Data.Any(d => d.Label == "Sector" && d.Values.Contains(sectorName))
                                 && r.Data.All(d => d.Label != "System" || d.Values.Count == 0)
                        ).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.PageTitle });
    }
}
