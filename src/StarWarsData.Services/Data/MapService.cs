using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StarWarsData.Models.Mongo;

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
            planets.Add(new GalaxyMapItem { Letter = letter, Number = number, Name = rec.PageTitle });
        }
        return planets;
    }

    public async Task<IEnumerable<GalaxyGridCell>> GetGalaxyGridCells()
    {
        var col = _db.GetCollection<Record>("CelestialBody");
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

            var sector = rec.Data.FirstOrDefault(d => d.Label == "Sector")?.Values.FirstOrDefault();
            var system = rec.Data.FirstOrDefault(d => d.Label == "System")?.Values.FirstOrDefault();
            var planet = new GalaxyMapItem { Letter = letter, Number = number, Name = rec.PageTitle };

            if (!grid.TryGetValue(key, out var cell))
            {
                cell = new GalaxyGridCell { Letter = letter, Number = number, Sector = sector };
                grid[key] = cell;
            }

            if (!string.IsNullOrWhiteSpace(system))
            {
                var sys = cell.Systems.FirstOrDefault(s => s.Name == system);
                if (sys == null)
                {
                    sys = new SystemWithPlanets { Name = system };
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
}
