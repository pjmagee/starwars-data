using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class MapService
{
    readonly ILogger<MapService> _logger;
    readonly IMongoCollection<Page> _pages;

    public MapService(
        ILogger<MapService> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient
    )
    {
        _logger = logger;
        _pages = mongoClient.GetDatabase(settingsOptions.Value.PagesDb).GetCollection<Page>("Pages");
    }

    static FilterDefinition<Page> TemplateFilter(string type) =>
        Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression($":{type}$", "i"));

    static FilterDefinition<Page> InfoboxDataFilter(string type, string label) =>
        Builders<Page>.Filter.And(
            TemplateFilter(type),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument("Label", label)
            )
        );

    static FilterDefinition<Page> InfoboxDataValueFilter(string type, string label, string value) =>
        Builders<Page>.Filter.And(
            TemplateFilter(type),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument
                {
                    { "Label", label },
                    { "Values", value },
                }
            )
        );

    static List<string> GetDataValues(Page page, string label) =>
        page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values ?? [];

    static string? GetFirstDataValue(Page page, string label) =>
        page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values.FirstOrDefault();

    public async Task<IEnumerable<GalaxyMapItem>> GetPlanets()
    {
        var planets = new List<GalaxyMapItem>();
        var gridFilter = InfoboxDataFilter("CelestialBody", "Grid square");
        var classFilter = InfoboxDataValueFilter("CelestialBody", "Class", "Terrestrial");
        var filter = Builders<Page>.Filter.And(gridFilter, classFilter);
        var recs = await _pages.Find(filter).ToListAsync();
        foreach (var rec in recs)
        {
            var prop = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Grid square");
            if (prop?.Values == null || prop.Values.Count == 0) continue;
            var loc = prop.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2) continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number)) continue;
            planets.Add(new GalaxyMapItem
            {
                Id = rec.PageId,
                Letter = letter,
                Number = number,
                Name = rec.Title,
            });
        }
        return planets;
    }

    public async Task<IEnumerable<GalaxyGridCell>> GetGalaxyGridCells(Continuity? continuity = null)
    {
        // Preload system, sector, and region name→Id maps
        var sysRecs = await _pages.Find(TemplateFilter("System")).ToListAsync();
        var systemMap = sysRecs.ToDictionary(r => r.Title, r => r.PageId);

        var sectorRecs = await _pages.Find(TemplateFilter("Sector")).ToListAsync();
        var sectorMap = sectorRecs.ToDictionary(r => r.Title, r => r.PageId);

        var regionRecs = await _pages.Find(TemplateFilter("Region")).ToListAsync();
        var regionMap = regionRecs.ToDictionary(r => r.Title, r => r.PageId);

        var gridFilter = InfoboxDataFilter("CelestialBody", "Grid square");
        var classFilter = InfoboxDataValueFilter("CelestialBody", "Class", "Terrestrial");
        var filter = Builders<Page>.Filter.And(gridFilter, classFilter);
        if (continuity.HasValue)
            filter = Builders<Page>.Filter.And(
                filter,
                Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
            );
        var recs = await _pages.Find(filter).ToListAsync();

        var grid = new Dictionary<string, GalaxyGridCell>();
        foreach (var rec in recs)
        {
            var gridProp = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Grid square");
            if (gridProp?.Values == null || gridProp.Values.Count == 0) continue;
            var loc = gridProp.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2) continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number)) continue;
            var key = $"{letter}-{number}";

            var sectorName = GetFirstDataValue(rec, "Sector");
            var regionName = GetFirstDataValue(rec, "Region");
            var system = GetFirstDataValue(rec, "System");
            var planet = new GalaxyMapItem
            {
                Id = rec.PageId,
                Letter = letter,
                Number = number,
                Name = rec.Title,
            };

            if (!grid.TryGetValue(key, out var cell))
            {
                cell = new GalaxyGridCell
                {
                    Letter = letter,
                    Number = number,
                    Sector = sectorName,
                    SectorId = sectorName != null && sectorMap.TryGetValue(sectorName, out var sid) ? sid : null,
                    Region = regionName,
                    RegionId = regionName != null && regionMap.TryGetValue(regionName, out var rid) ? rid : null,
                };
                grid[key] = cell;
            }

            if (!string.IsNullOrWhiteSpace(system))
            {
                var sys = cell.Systems.FirstOrDefault(s => s.Name == system);
                if (sys == null)
                {
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

        // Overlay nebulas
        var nebulaFilter = InfoboxDataFilter("Nebula", "Grid square");
        if (continuity.HasValue)
            nebulaFilter = Builders<Page>.Filter.And(
                nebulaFilter,
                Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
            );
        var nebulaRecs = await _pages.Find(nebulaFilter).ToListAsync();
        foreach (var rec in nebulaRecs)
        {
            var gridProp = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Grid square");
            if (gridProp?.Values == null || gridProp.Values.Count == 0) continue;
            var loc = gridProp.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2) continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number)) continue;
            var key = $"{letter}-{number}";
            if (!grid.TryGetValue(key, out var cell))
            {
                cell = new GalaxyGridCell { Letter = letter, Number = number };
                grid[key] = cell;
            }
            cell.Nebulas.Add(new GalaxyMapItem
            {
                Id = rec.PageId,
                Letter = letter,
                Number = number,
                Name = rec.Title,
            });
        }

        return grid.Values;
    }

    public async Task<IEnumerable<SectorDto>> GetSectorsAsync()
    {
        var recs = await _pages.Find(TemplateFilter("Sector")).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<SectorDto?> GetSectorAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter("Sector"), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null) return null;
        return new SectorDto { Id = rec.PageId, Name = rec.Title };
    }

    public async Task<IEnumerable<RegionDto>> GetRegionsAsync()
    {
        var recs = await _pages.Find(TemplateFilter("Region")).ToListAsync();
        return recs.Select(r => new RegionDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<RegionDto?> GetRegionAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter("Region"), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null) return null;
        return new RegionDto { Id = rec.PageId, Name = rec.Title };
    }

    public async Task<SystemDetailsDto?> GetSystemDetailsAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter("System"), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null) return null;
        var grid = GetFirstDataValue(rec, "Grid square");
        var planets = GetDataValues(rec, "Planets");
        var neighbors = GetDataValues(rec, "Neighboring systems");
        return new SystemDetailsDto
        {
            Id = rec.PageId,
            Name = rec.Title,
            GridSquare = grid,
            Planets = planets,
            Neighbors = neighbors,
        };
    }

    public async Task<CelestialBodyDetailsDto?> GetCelestialBodyDetailsAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter("CelestialBody"), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null) return null;
        var data = rec.Infobox?.Data ?? [];
        var cls = data.FirstOrDefault(d => d.Label == "Class")?.Values.FirstOrDefault() ?? string.Empty;
        var grid = GetFirstDataValue(rec, "Grid square");
        var sector = GetFirstDataValue(rec, "Sector");
        var region = GetFirstDataValue(rec, "Region");
        var additional = data
            .Where(d => d.Label is not null && !new[] { "Class", "Grid square", "Sector", "Region" }.Contains(d.Label))
            .ToDictionary(d => d.Label!, d => d.Values);
        return new CelestialBodyDetailsDto
        {
            Id = rec.PageId,
            Name = rec.Title,
            Class = cls,
            GridSquare = grid,
            Sector = sector,
            Region = region,
            AdditionalData = additional,
        };
    }

    public async Task<NebulaDetailsDto?> GetNebulaDetailsAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter("Nebula"), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null) return null;
        var data = rec.Infobox?.Data ?? [];
        var grid = GetFirstDataValue(rec, "Grid square");
        var sector = GetFirstDataValue(rec, "Sector");
        var region = GetFirstDataValue(rec, "Region");
        var additional = data
            .Where(d => d.Label is not null && !new[] { "Grid square", "Sector", "Region" }.Contains(d.Label))
            .ToDictionary(d => d.Label!, d => d.Values);
        return new NebulaDetailsDto
        {
            Id = rec.PageId,
            Name = rec.Title,
            GridSquare = grid,
            Sector = sector,
            Region = region,
            AdditionalData = additional,
        };
    }

    public async Task<IEnumerable<SectorDto>> GetSectorsByRegionAsync(int regionId)
    {
        var regionFilter = Builders<Page>.Filter.And(TemplateFilter("Region"), Builders<Page>.Filter.Eq(p => p.PageId, regionId));
        var region = await _pages.Find(regionFilter).FirstOrDefaultAsync();
        if (region == null) return Enumerable.Empty<SectorDto>();
        var regionName = region.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter("Sector"),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument { { "Label", "Region(s)" }, { "Values", regionName } }
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<SystemDto>> GetSystemsBySectorAsync(int sectorId)
    {
        var sectorFilter = Builders<Page>.Filter.And(TemplateFilter("Sector"), Builders<Page>.Filter.Eq(p => p.PageId, sectorId));
        var sector = await _pages.Find(sectorFilter).FirstOrDefaultAsync();
        if (sector == null) return Enumerable.Empty<SystemDto>();
        var sectorName = sector.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter("System"),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument { { "Label", "Sector" }, { "Values", sectorName } }
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new SystemDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetPlanetsBySystemAsync(int systemId)
    {
        var sysFilter = Builders<Page>.Filter.And(TemplateFilter("System"), Builders<Page>.Filter.Eq(p => p.PageId, systemId));
        var system = await _pages.Find(sysFilter).FirstOrDefaultAsync();
        if (system == null) return Enumerable.Empty<PlanetDto>();
        var systemName = system.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter("CelestialBody"),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument { { "Label", "System" }, { "Values", systemName } }
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInRegionAsync(int regionId)
    {
        var regionFilter = Builders<Page>.Filter.And(TemplateFilter("Region"), Builders<Page>.Filter.Eq(p => p.PageId, regionId));
        var region = await _pages.Find(regionFilter).FirstOrDefaultAsync();
        if (region == null) return Enumerable.Empty<PlanetDto>();
        var regionName = region.Title;
        // CelestialBody with Region matching but no System
        var filter = Builders<Page>.Filter.And(
            TemplateFilter("CelestialBody"),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument { { "Label", "Region" }, { "Values", regionName } }
            ),
            Builders<Page>.Filter.Not(
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument
                    {
                        { "Label", "System" },
                        { "Values", new BsonDocument("$exists", true) },
                        { "Values.0", new BsonDocument("$exists", true) },
                    }
                )
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInSectorAsync(int sectorId)
    {
        var sectorFilter = Builders<Page>.Filter.And(TemplateFilter("Sector"), Builders<Page>.Filter.Eq(p => p.PageId, sectorId));
        var sector = await _pages.Find(sectorFilter).FirstOrDefaultAsync();
        if (sector == null) return Enumerable.Empty<PlanetDto>();
        var sectorName = sector.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter("CelestialBody"),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument { { "Label", "Sector" }, { "Values", sectorName } }
            ),
            Builders<Page>.Filter.Not(
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument
                    {
                        { "Label", "System" },
                        { "Values", new BsonDocument("$exists", true) },
                        { "Values.0", new BsonDocument("$exists", true) },
                    }
                )
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.Title });
    }

    // Labels on non-spatial entities that reference locations
    static readonly string[] LocationLabels =
    [
        "Homeworld", "Location", "Planet", "Birthplace", "Capital",
        "Headquarters", "Base of operations", "Place", "Theater",
        "Born", "Died", "Destroyed", "System", "Sector", "Region",
        "Base", "World", "Moon", "Located"
    ];

    public async Task<List<MapSearchResult>> SearchGridAsync(string term, Continuity? continuity = null)
    {
        var escaped = Regex.Escape(term);
        var results = new List<MapSearchResult>();
        var seen = new HashSet<string>(); // dedupe grid keys

        // 1. Direct matches: entities WITH "Grid square" whose title or content matches
        //    Use $text search for relevance-ranked results across title + content
        var directTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data",
                new BsonDocument("Label", "Grid square")
            )
        );
        if (continuity.HasValue)
            directTextFilter = Builders<Page>.Filter.And(
                directTextFilter,
                Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
            );

        var directMatches = await _pages
            .Find(directTextFilter)
            .Sort(Builders<Page>.Sort.MetaTextScore("score"))
            .Limit(50)
            .ToListAsync();

        // Fallback to regex on title if text search returns nothing
        if (directMatches.Count == 0)
        {
            var directRegexFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.Regex("title", new BsonRegularExpression(escaped, "i")),
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument("Label", "Grid square")
                )
            );
            if (continuity.HasValue)
                directRegexFilter = Builders<Page>.Filter.And(
                    directRegexFilter,
                    Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
                );
            directMatches = await _pages.Find(directRegexFilter).Limit(50).ToListAsync();
        }
        foreach (var page in directMatches)
        {
            var gridSquare = GetFirstDataValue(page, "Grid square");
            if (gridSquare == null) continue;
            var parts = gridSquare.Split('-', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out _)) continue;
            var gridKey = $"{parts[0].Trim()}-{parts[1].Trim()}";

            var template = page.Infobox?.Template?.Split(':').LastOrDefault();
            results.Add(new MapSearchResult
            {
                GridKey = gridKey,
                PageId = page.PageId,
                MatchedName = page.Title,
                Template = template,
                MatchType = "direct",
            });
            seen.Add(gridKey);
        }

        // 2. Indirect matches: entities WITHOUT grid squares whose title or content matches
        //    We look at their location-related infobox properties to find referenced places
        var indirectTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.Not(
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument("Label", "Grid square")
                )
            ),
            Builders<Page>.Filter.Ne("infobox", BsonNull.Value)
        );
        if (continuity.HasValue)
            indirectTextFilter = Builders<Page>.Filter.And(
                indirectTextFilter,
                Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
            );

        var indirectMatches = await _pages
            .Find(indirectTextFilter)
            .Sort(Builders<Page>.Sort.MetaTextScore("score"))
            .Limit(50)
            .ToListAsync();

        // Fallback to regex on title
        if (indirectMatches.Count == 0)
        {
            var indirectRegexFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.Regex("title", new BsonRegularExpression(escaped, "i")),
                Builders<Page>.Filter.Not(
                    Builders<Page>.Filter.ElemMatch<BsonDocument>(
                        "infobox.Data",
                        new BsonDocument("Label", "Grid square")
                    )
                ),
                Builders<Page>.Filter.Ne("infobox", BsonNull.Value)
            );
            if (continuity.HasValue)
                indirectRegexFilter = Builders<Page>.Filter.And(
                    indirectRegexFilter,
                    Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value)
                );
            indirectMatches = await _pages.Find(indirectRegexFilter).Limit(50).ToListAsync();
        }

        // Collect all referenced location names from infobox properties
        var locationRefs = new Dictionary<string, List<(int sourcePageId, string entityName, string label)>>();
        foreach (var page in indirectMatches)
        {
            if (page.Infobox?.Data == null) continue;
            foreach (var prop in page.Infobox.Data)
            {
                if (prop.Label == null) continue;
                if (!LocationLabels.Any(l => prop.Label.Contains(l, StringComparison.OrdinalIgnoreCase))) continue;

                // Collect values and link contents as potential location names
                var names = new List<string>();
                names.AddRange(prop.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
                names.AddRange(prop.Links.Select(l => l.Content).Where(c => !string.IsNullOrWhiteSpace(c)));

                foreach (var name in names.Distinct())
                {
                    if (!locationRefs.ContainsKey(name))
                        locationRefs[name] = [];
                    locationRefs[name].Add((page.PageId, page.Title, prop.Label));
                }
            }
        }

        if (locationRefs.Count > 0)
        {
            // Batch-lookup all referenced location names to find their grid squares
            var nameFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.In("title", locationRefs.Keys),
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument("Label", "Grid square")
                )
            );
            var resolved = await _pages.Find(nameFilter).ToListAsync();

            foreach (var page in resolved)
            {
                var gridSquare = GetFirstDataValue(page, "Grid square");
                if (gridSquare == null) continue;
                var parts = gridSquare.Split('-', 2);
                if (parts.Length != 2 || !int.TryParse(parts[1], out _)) continue;
                var gridKey = $"{parts[0].Trim()}-{parts[1].Trim()}";

                if (!locationRefs.TryGetValue(page.Title, out var refs)) continue;
                foreach (var (sourcePageId, entityName, label) in refs)
                {
                    results.Add(new MapSearchResult
                    {
                        GridKey = gridKey,
                        PageId = page.PageId,
                        MatchedName = page.Title,
                        Template = page.Infobox?.Template?.Split(':').LastOrDefault(),
                        MatchType = "linked",
                        LinkedVia = $"{label} of {entityName}",
                        SourcePageId = sourcePageId,
                        SourceName = entityName,
                    });
                    seen.Add(gridKey);
                }
            }
        }

        return results;
    }
}
