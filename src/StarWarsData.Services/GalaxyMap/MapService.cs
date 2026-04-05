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
    readonly SemanticSearchService _semanticSearch;

    public MapService(ILogger<MapService> logger, IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient, SemanticSearchService semanticSearch)
    {
        _logger = logger;
        _pages = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName).GetCollection<Page>(Collections.Pages);
        _semanticSearch = semanticSearch;
    }

    static FilterDefinition<Page> TemplateFilter(string type) => Builders<Page>.Filter.Eq("infobox.Template", $"{Collections.TemplateUrlPrefix}{type}");

    static FilterDefinition<Page> InfoboxDataFilter(string type, string label) =>
        Builders<Page>.Filter.And(TemplateFilter(type), Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, label)));

    static FilterDefinition<Page> InfoboxDataValueFilter(string type, string label, string value) =>
        Builders<Page>.Filter.And(
            TemplateFilter(type),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, label }, { InfoboxBsonFields.Values, value } })
        );

    static List<string> GetDataValues(Page page, string label) => page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values ?? [];

    static string? GetFirstDataValue(Page page, string label) => page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values.FirstOrDefault();

    /// <summary>
    /// Normalize region names to merge duplicates in the wiki data.
    /// E.g. "Mid Rim" and "Mid Rim Territories" are the same region,
    /// as are "Inner Rim" / "Inner Rim Territories", case variants, and compound entries.
    /// </summary>
    internal static string NormalizeRegionName(string region)
    {
        // Strip compound suffixes like "; Inner Zuma Region", ", Greater Javin"
        var sepIdx = region.IndexOfAny([';', ',']);
        if (sepIdx > 0)
            region = region[..sepIdx].Trim();

        // Normalize case
        region = region.Trim();

        // Merge "X" / "X Territories" variants
        if (region.EndsWith(" Territories", StringComparison.OrdinalIgnoreCase))
            region = region[..^" Territories".Length];

        // Canonical names
        return region.ToLowerInvariant() switch
        {
            "outer rim" => "Outer Rim Territories",
            "mid rim" => "Mid Rim",
            "inner rim" => "Inner Rim",
            "the slice" => "The Slice",
            "the interior" => "The Interior",
            _ => region,
        };
    }

    public async Task<IEnumerable<GalaxyMapItem>> GetPlanets()
    {
        var planets = new List<GalaxyMapItem>();
        var gridFilter = InfoboxDataFilter(KgNodeTypes.CelestialBody, InfoboxFieldLabels.GridSquare);
        var classFilter = InfoboxDataValueFilter(KgNodeTypes.CelestialBody, InfoboxFieldLabels.Class, "Terrestrial");
        var filter = Builders<Page>.Filter.And(gridFilter, classFilter);
        var recs = await _pages.Find(filter).ToListAsync();
        foreach (var rec in recs)
        {
            var prop = rec.Infobox?.Data.FirstOrDefault(d => d.Label == InfoboxFieldLabels.GridSquare);
            if (prop?.Values == null || prop.Values.Count == 0)
                continue;
            var loc = prop.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2)
                continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number))
                continue;
            planets.Add(
                new GalaxyMapItem
                {
                    Id = rec.PageId,
                    Letter = letter,
                    Number = number,
                    Name = rec.Title,
                }
            );
        }
        return planets;
    }

    public async Task<IEnumerable<GalaxyGridCell>> GetGalaxyGridCells(Continuity? continuity = null)
    {
        // Preload system, sector, and region name→Id maps
        var sysRecs = await _pages.Find(TemplateFilter(KgNodeTypes.System)).ToListAsync();
        var systemMap = sysRecs.ToDictionary(r => r.Title, r => r.PageId);

        var sectorRecs = await _pages.Find(TemplateFilter(KgNodeTypes.Sector)).ToListAsync();
        var sectorMap = sectorRecs.ToDictionary(r => r.Title, r => r.PageId);

        var regionRecs = await _pages.Find(TemplateFilter(KgNodeTypes.Region)).ToListAsync();
        var regionMap = regionRecs.ToDictionary(r => r.Title, r => r.PageId);

        var gridFilter = InfoboxDataFilter(KgNodeTypes.CelestialBody, InfoboxFieldLabels.GridSquare);
        var classFilter = InfoboxDataValueFilter(KgNodeTypes.CelestialBody, InfoboxFieldLabels.Class, "Terrestrial");
        var filter = Builders<Page>.Filter.And(gridFilter, classFilter);
        if (continuity.HasValue)
            filter = Builders<Page>.Filter.And(filter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var recs = await _pages.Find(filter).ToListAsync();

        var grid = new Dictionary<string, GalaxyGridCell>();
        foreach (var rec in recs)
        {
            var gridProp = rec.Infobox?.Data.FirstOrDefault(d => d.Label == InfoboxFieldLabels.GridSquare);
            if (gridProp?.Values == null || gridProp.Values.Count == 0)
                continue;
            var loc = gridProp.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2)
                continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number))
                continue;
            var key = $"{letter}-{number}";

            var sectorName = GetFirstDataValue(rec, InfoboxFieldLabels.Sector);
            var regionName = GetFirstDataValue(rec, InfoboxFieldLabels.Region);
            var system = GetFirstDataValue(rec, InfoboxFieldLabels.System);
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
        var nebulaFilter = InfoboxDataFilter(KgNodeTypes.Nebula, InfoboxFieldLabels.GridSquare);
        if (continuity.HasValue)
            nebulaFilter = Builders<Page>.Filter.And(nebulaFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var nebulaRecs = await _pages.Find(nebulaFilter).ToListAsync();
        foreach (var rec in nebulaRecs)
        {
            var gridProp = rec.Infobox?.Data.FirstOrDefault(d => d.Label == InfoboxFieldLabels.GridSquare);
            if (gridProp?.Values == null || gridProp.Values.Count == 0)
                continue;
            var loc = gridProp.Values.First();
            var parts = loc.Split('-', 2);
            if (parts.Length != 2)
                continue;
            var letter = parts[0].Trim();
            if (!int.TryParse(parts[1], out var number))
                continue;
            var key = $"{letter}-{number}";
            if (!grid.TryGetValue(key, out var cell))
            {
                cell = new GalaxyGridCell { Letter = letter, Number = number };
                grid[key] = cell;
            }
            cell.Nebulas.Add(
                new GalaxyMapItem
                {
                    Id = rec.PageId,
                    Letter = letter,
                    Number = number,
                    Name = rec.Title,
                }
            );
        }

        return grid.Values;
    }

    public async Task<IEnumerable<SectorDto>> GetSectorsAsync()
    {
        var recs = await _pages.Find(TemplateFilter(KgNodeTypes.Sector)).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<SectorDto?> GetSectorAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Sector), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null)
            return null;
        return new SectorDto { Id = rec.PageId, Name = rec.Title };
    }

    public async Task<IEnumerable<RegionDto>> GetRegionsAsync()
    {
        var recs = await _pages.Find(TemplateFilter(KgNodeTypes.Region)).ToListAsync();
        return recs.Select(r => new RegionDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<RegionDto?> GetRegionAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Region), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null)
            return null;
        return new RegionDto { Id = rec.PageId, Name = rec.Title };
    }

    public async Task<SystemDetailsDto?> GetSystemDetailsAsync(int id)
    {
        var filter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.System), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null)
            return null;
        var grid = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
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
        var filter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.CelestialBody), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null)
            return null;
        var data = rec.Infobox?.Data ?? [];
        var cls = data.FirstOrDefault(d => d.Label == InfoboxFieldLabels.Class)?.Values.FirstOrDefault() ?? string.Empty;
        var grid = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
        var sector = GetFirstDataValue(rec, InfoboxFieldLabels.Sector);
        var region = GetFirstDataValue(rec, InfoboxFieldLabels.Region);
        var additional = data.Where(d =>
                d.Label is not null && !new[] { InfoboxFieldLabels.Class, InfoboxFieldLabels.GridSquare, InfoboxFieldLabels.Sector, InfoboxFieldLabels.Region }.Contains(d.Label)
            )
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
        var filter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Nebula), Builders<Page>.Filter.Eq(p => p.PageId, id));
        var rec = await _pages.Find(filter).FirstOrDefaultAsync();
        if (rec == null)
            return null;
        var data = rec.Infobox?.Data ?? [];
        var grid = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
        var sector = GetFirstDataValue(rec, InfoboxFieldLabels.Sector);
        var region = GetFirstDataValue(rec, InfoboxFieldLabels.Region);
        var additional = data.Where(d => d.Label is not null && !new[] { InfoboxFieldLabels.GridSquare, InfoboxFieldLabels.Sector, InfoboxFieldLabels.Region }.Contains(d.Label))
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
        var regionFilter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Region), Builders<Page>.Filter.Eq(p => p.PageId, regionId));
        var region = await _pages.Find(regionFilter).FirstOrDefaultAsync();
        if (region == null)
            return Enumerable.Empty<SectorDto>();
        var regionName = region.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter(KgNodeTypes.Sector),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.RegionPlural }, { InfoboxBsonFields.Values, regionName } })
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new SectorDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<SystemDto>> GetSystemsBySectorAsync(int sectorId)
    {
        var sectorFilter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Sector), Builders<Page>.Filter.Eq(p => p.PageId, sectorId));
        var sector = await _pages.Find(sectorFilter).FirstOrDefaultAsync();
        if (sector == null)
            return Enumerable.Empty<SystemDto>();
        var sectorName = sector.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter(KgNodeTypes.System),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.Sector }, { InfoboxBsonFields.Values, sectorName } })
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new SystemDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetPlanetsBySystemAsync(int systemId)
    {
        var sysFilter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.System), Builders<Page>.Filter.Eq(p => p.PageId, systemId));
        var system = await _pages.Find(sysFilter).FirstOrDefaultAsync();
        if (system == null)
            return Enumerable.Empty<PlanetDto>();
        var systemName = system.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter(KgNodeTypes.CelestialBody),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.System }, { InfoboxBsonFields.Values, systemName } })
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInRegionAsync(int regionId)
    {
        var regionFilter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Region), Builders<Page>.Filter.Eq(p => p.PageId, regionId));
        var region = await _pages.Find(regionFilter).FirstOrDefaultAsync();
        if (region == null)
            return Enumerable.Empty<PlanetDto>();
        var regionName = region.Title;
        // CelestialBody with Region matching but no System
        var filter = Builders<Page>.Filter.And(
            TemplateFilter(KgNodeTypes.CelestialBody),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.Region }, { InfoboxBsonFields.Values, regionName } }),
            Builders<Page>.Filter.Not(
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument
                    {
                        { InfoboxBsonFields.Label, InfoboxFieldLabels.System },
                        { InfoboxBsonFields.Values, new BsonDocument("$exists", true) },
                        { InfoboxBsonFields.ValuesFirst, new BsonDocument("$exists", true) },
                    }
                )
            )
        );
        var recs = await _pages.Find(filter).ToListAsync();
        return recs.Select(r => new PlanetDto { Id = r.PageId, Name = r.Title });
    }

    public async Task<IEnumerable<PlanetDto>> GetOrphanPlanetsInSectorAsync(int sectorId)
    {
        var sectorFilter = Builders<Page>.Filter.And(TemplateFilter(KgNodeTypes.Sector), Builders<Page>.Filter.Eq(p => p.PageId, sectorId));
        var sector = await _pages.Find(sectorFilter).FirstOrDefaultAsync();
        if (sector == null)
            return Enumerable.Empty<PlanetDto>();
        var sectorName = sector.Title;
        var filter = Builders<Page>.Filter.And(
            TemplateFilter(KgNodeTypes.CelestialBody),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument { { InfoboxBsonFields.Label, InfoboxFieldLabels.Sector }, { InfoboxBsonFields.Values, sectorName } }),
            Builders<Page>.Filter.Not(
                Builders<Page>.Filter.ElemMatch<BsonDocument>(
                    "infobox.Data",
                    new BsonDocument
                    {
                        { InfoboxBsonFields.Label, InfoboxFieldLabels.System },
                        { InfoboxBsonFields.Values, new BsonDocument("$exists", true) },
                        { InfoboxBsonFields.ValuesFirst, new BsonDocument("$exists", true) },
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
        "Homeworld",
        "Location",
        "Planet",
        "Birthplace",
        "Capital",
        "Headquarters",
        "Base of operations",
        "Place",
        "Theater",
        "Born",
        "Died",
        "Destroyed",
        "System",
        "Sector",
        "Region",
        "Base",
        "World",
        "Moon",
        "Located",
    ];

    public async Task<List<MapSearchResult>> SearchGridAsync(string term, Continuity? continuity = null)
    {
        var escaped = Regex.Escape(term);
        var results = new List<MapSearchResult>();
        var seen = new HashSet<string>(); // dedupe grid keys

        // 1. Direct matches: entities WITH InfoboxFieldLabels.GridSquare whose title or content matches
        //    Use $text search for relevance-ranked results across title + content
        var directTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
        );
        if (continuity.HasValue)
            directTextFilter = Builders<Page>.Filter.And(directTextFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));

        var directMatches = await _pages.Find(directTextFilter).Sort(Builders<Page>.Sort.MetaTextScore("score")).Limit(50).ToListAsync();

        // Fallback to regex on title if text search returns nothing
        if (directMatches.Count == 0)
        {
            var directRegexFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.Regex(PageBsonFields.Title, new BsonRegularExpression(escaped, "i")),
                Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
            );
            if (continuity.HasValue)
                directRegexFilter = Builders<Page>.Filter.And(directRegexFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
            directMatches = await _pages.Find(directRegexFilter).Limit(50).ToListAsync();
        }
        foreach (var page in directMatches)
        {
            var gridSquare = GetFirstDataValue(page, InfoboxFieldLabels.GridSquare);
            if (gridSquare == null)
                continue;
            var parts = gridSquare.Split('-', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out _))
                continue;
            var gridKey = $"{parts[0].Trim()}-{parts[1].Trim()}";

            var template = page.Infobox?.Template?.Split(':').LastOrDefault();
            results.Add(
                new MapSearchResult
                {
                    GridKey = gridKey,
                    PageId = page.PageId,
                    MatchedName = page.Title,
                    Template = template,
                    MatchType = "direct",
                }
            );
            seen.Add(gridKey);
        }

        // 2. Indirect matches: entities WITHOUT grid squares whose title or content matches
        //    We look at their location-related infobox properties to find referenced places
        var indirectTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.Not(Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))),
            Builders<Page>.Filter.Ne(PageBsonFields.Infobox, BsonNull.Value)
        );
        if (continuity.HasValue)
            indirectTextFilter = Builders<Page>.Filter.And(indirectTextFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));

        var indirectMatches = await _pages.Find(indirectTextFilter).Sort(Builders<Page>.Sort.MetaTextScore("score")).Limit(50).ToListAsync();

        // Fallback to regex on title
        if (indirectMatches.Count == 0)
        {
            var indirectRegexFilter = Builders<Page>.Filter.And(
                Builders<Page>.Filter.Regex(PageBsonFields.Title, new BsonRegularExpression(escaped, "i")),
                Builders<Page>.Filter.Not(Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))),
                Builders<Page>.Filter.Ne(PageBsonFields.Infobox, BsonNull.Value)
            );
            if (continuity.HasValue)
                indirectRegexFilter = Builders<Page>.Filter.And(indirectRegexFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
            indirectMatches = await _pages.Find(indirectRegexFilter).Limit(50).ToListAsync();
        }

        // Collect all referenced location names from infobox properties
        var locationRefs = new Dictionary<string, List<(int sourcePageId, string entityName, string label)>>();
        foreach (var page in indirectMatches)
        {
            if (page.Infobox?.Data == null)
                continue;
            foreach (var prop in page.Infobox.Data)
            {
                if (prop.Label == null)
                    continue;
                if (!LocationLabels.Any(l => prop.Label.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    continue;

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
                Builders<Page>.Filter.In(PageBsonFields.Title, locationRefs.Keys),
                Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
            );
            var resolved = await _pages.Find(nameFilter).ToListAsync();

            foreach (var page in resolved)
            {
                var gridSquare = GetFirstDataValue(page, InfoboxFieldLabels.GridSquare);
                if (gridSquare == null)
                    continue;
                var parts = gridSquare.Split('-', 2);
                if (parts.Length != 2 || !int.TryParse(parts[1], out _))
                    continue;
                var gridKey = $"{parts[0].Trim()}-{parts[1].Trim()}";

                if (!locationRefs.TryGetValue(page.Title, out var refs))
                    continue;
                foreach (var (sourcePageId, entityName, label) in refs)
                {
                    results.Add(
                        new MapSearchResult
                        {
                            GridKey = gridKey,
                            PageId = page.PageId,
                            MatchedName = page.Title,
                            Template = page.Infobox?.Template?.Split(':').LastOrDefault(),
                            MatchType = "linked",
                            LinkedVia = $"{label} of {entityName}",
                            SourcePageId = sourcePageId,
                            SourceName = entityName,
                        }
                    );
                    seen.Add(gridKey);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Semantic search for the galaxy map: delegates to SemanticSearchService,
    /// then resolves results to grid-square-placeable entities.
    /// </summary>
    public async Task<List<MapSearchResult>> SemanticSearchGridAsync(string query, Continuity? continuity = null, int limit = 10)
    {
        // Fetch more results than needed — many won't have grid squares.
        // minScore 0.6 filters out low-relevance noise.
        const double minScore = 0.6;
        var spatialResults = await _semanticSearch.SearchAsync(query, [KgNodeTypes.System, KgNodeTypes.CelestialBody], continuity, limit: limit * 2, minScore: minScore);
        var allResults = await _semanticSearch.SearchAsync(query, null, continuity, limit: limit * 3, minScore: minScore);

        _logger.LogInformation("SemanticSearchGridAsync: query={Query}, spatialHits={Spatial}, allHits={All}", query, spatialResults.Count, allResults.Count);

        // Merge, spatial first (higher priority for map placement)
        var pageScores = new Dictionary<int, SemanticSearchResult>();
        foreach (var r in spatialResults.Concat(allResults))
        {
            pageScores.TryAdd(r.PageId, r);
        }

        if (pageScores.Count == 0)
            return [];

        // ── Resolve to grid squares ──
        var pageIds = pageScores.Keys.ToList();
        var gridFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.In(p => p.PageId, pageIds),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
        );
        var pagesWithGrid = await _pages
            .Find(gridFilter)
            .Project<Page>(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title).Include("infobox.Data").Include("infobox.Template"))
            .ToListAsync();

        var results = new List<MapSearchResult>();
        var seenPages = new HashSet<int>();

        foreach (var page in pagesWithGrid)
        {
            var gridSquare = GetFirstDataValue(page, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridSquare, out var col, out var row))
                continue;
            if (!pageScores.TryGetValue(page.PageId, out var info))
                continue;

            seenPages.Add(page.PageId);
            var text = info.Text;
            results.Add(
                new MapSearchResult
                {
                    GridKey = $"{(char)('A' + col)}-{row + 1}",
                    PageId = page.PageId,
                    MatchedName = page.Title,
                    Template = page.Infobox?.Template?.Split(':').LastOrDefault(),
                    MatchType = "semantic",
                    Snippet = text.Length > 200 ? text[..200] + "..." : text,
                    Score = info.Score,
                }
            );
        }

        // Indirect: pages without grid squares → follow infobox location links
        var pagesWithoutGrid = pageIds.Where(id => !seenPages.Contains(id)).ToList();
        if (pagesWithoutGrid.Count > 0)
        {
            var noGridPages = await _pages
                .Find(Builders<Page>.Filter.In(p => p.PageId, pagesWithoutGrid))
                .Project<Page>(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title).Include("infobox.Data"))
                .ToListAsync();

            var locationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in noGridPages)
            {
                if (page.Infobox?.Data is null)
                    continue;
                foreach (var prop in page.Infobox.Data.Where(d => LocationLabels.Contains(d.Label)))
                foreach (var link in prop.Links)
                    locationNames.Add(link.Content);
            }

            if (locationNames.Count > 0)
            {
                var locPages = await _pages
                    .Find(
                        Builders<Page>.Filter.And(
                            Builders<Page>.Filter.In(PageBsonFields.Title, locationNames),
                            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
                        )
                    )
                    .Project<Page>(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title).Include("infobox.Data").Include("infobox.Template"))
                    .ToListAsync();

                foreach (var locPage in locPages)
                {
                    if (seenPages.Contains(locPage.PageId))
                        continue;
                    var gridSquare = GetFirstDataValue(locPage, InfoboxFieldLabels.GridSquare);
                    if (!TryParseGridSquare(gridSquare, out var col, out var row))
                        continue;

                    var sourcePage = noGridPages.FirstOrDefault(p =>
                        p.Infobox?.Data?.Any(d => LocationLabels.Contains(d.Label) && d.Links.Any(l => l.Content.Equals(locPage.Title, StringComparison.OrdinalIgnoreCase))) == true
                    );
                    if (sourcePage is null || !pageScores.TryGetValue(sourcePage.PageId, out var info))
                        continue;

                    seenPages.Add(locPage.PageId);
                    var text = info.Text;
                    results.Add(
                        new MapSearchResult
                        {
                            GridKey = $"{(char)('A' + col)}-{row + 1}",
                            PageId = locPage.PageId,
                            MatchedName = locPage.Title,
                            Template = locPage.Infobox?.Template?.Split(':').LastOrDefault(),
                            MatchType = "semantic-linked",
                            LinkedVia = sourcePage.Title,
                            SourcePageId = sourcePage.PageId,
                            SourceName = sourcePage.Title,
                            Snippet = text.Length > 200 ? text[..200] + "..." : text,
                            Score = info.Score,
                        }
                    );
                }
            }
        }

        // Direct semantic matches (the entity's own article matched) rank above
        // indirect linked matches (a related entity's article matched and linked here)
        return results.OrderByDescending(r => r.MatchType == "semantic" ? 1 : 0).ThenByDescending(r => r.Score).Take(limit).ToList();
    }

    static bool TryParseGridSquare(string? gridSquare, out int col, out int row)
    {
        col = 0;
        row = 0;
        if (string.IsNullOrWhiteSpace(gridSquare))
            return false;
        var parts = gridSquare.Split('-', 2);
        if (parts.Length != 2)
            return false;
        var letter = parts[0].Trim().ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z')
            return false;
        if (!int.TryParse(parts[1].Trim(), out var num) || num < 1 || num > 20)
            return false;
        col = letter[0] - 'A';
        row = num - 1;
        return true;
    }

    /// <summary>
    /// Lightweight overview: regions, trade routes, nebulas. No systems.
    /// Called once on page load (~300 elements).
    /// </summary>
    public async Task<GalaxyMapV2Overview> GetGalaxyMapV2OverviewAsync(Continuity? continuity = null)
    {
        var result = new GalaxyMapV2Overview();

        // Load all systems server-side to compute region boundaries and resolve trade route waypoints.
        // We do NOT return them to the client — only the derived data.
        var sysFilter = InfoboxDataFilter(KgNodeTypes.System, InfoboxFieldLabels.GridSquare);
        if (continuity.HasValue)
            sysFilter = Builders<Page>.Filter.And(sysFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var sysRecs = await _pages.Find(sysFilter).Project<Page>(Builders<Page>.Projection.Include(p => p.Title).Include(p => p.PageId).Include("infobox.Data")).ToListAsync();

        var nameToGrid = new Dictionary<string, (int col, int row)>(StringComparer.OrdinalIgnoreCase);
        var regionCells = new Dictionary<string, HashSet<(int col, int row)>>(StringComparer.OrdinalIgnoreCase);
        // Track per-cell system count and dominant region
        var cellCounts = new Dictionary<(int col, int row), int>();
        var cellRegion = new Dictionary<(int col, int row), Dictionary<string, int>>();

        foreach (var rec in sysRecs)
        {
            var gridSquare = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridSquare, out var col, out var row))
                continue;
            nameToGrid.TryAdd(rec.Title, (col, row));

            var key = (col, row);
            cellCounts[key] = cellCounts.GetValueOrDefault(key) + 1;

            var regionRaw = GetFirstDataValue(rec, InfoboxFieldLabels.Region);
            if (!string.IsNullOrEmpty(regionRaw))
            {
                var region = NormalizeRegionName(regionRaw);
                if (!regionCells.TryGetValue(region, out var cells))
                {
                    cells = [];
                    regionCells[region] = cells;
                }
                cells.Add((col, row));

                if (!cellRegion.TryGetValue(key, out var regionCount))
                {
                    regionCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    cellRegion[key] = regionCount;
                }
                regionCount[region] = regionCount.GetValueOrDefault(region) + 1;
            }
        }

        // Also index celestial bodies for trade route resolution
        var cbFilter = InfoboxDataFilter(KgNodeTypes.CelestialBody, InfoboxFieldLabels.GridSquare);
        if (continuity.HasValue)
            cbFilter = Builders<Page>.Filter.And(cbFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var cbRecs = await _pages.Find(cbFilter).Project<Page>(Builders<Page>.Projection.Include(p => p.Title).Include("infobox.Data")).ToListAsync();

        foreach (var rec in cbRecs)
        {
            var gridSquare = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridSquare, out var col, out var row))
                continue;
            nameToGrid.TryAdd(rec.Title, (col, row));

            var regionRaw = GetFirstDataValue(rec, InfoboxFieldLabels.Region);
            if (!string.IsNullOrEmpty(regionRaw))
            {
                var region = NormalizeRegionName(regionRaw);
                if (!regionCells.TryGetValue(region, out var cells))
                {
                    cells = [];
                    regionCells[region] = cells;
                }
                cells.Add((col, row));
            }
        }

        // Build regions
        foreach (var (name, cells) in regionCells)
        {
            result.Regions.Add(new MapV2Region { Name = name, Cells = cells.Select(c => new[] { c.col, c.row }).ToList() });
        }

        // Build cell summaries
        foreach (var (key, count) in cellCounts)
        {
            string? dominantRegion = null;
            if (cellRegion.TryGetValue(key, out var regionCount))
                dominantRegion = regionCount.MaxBy(kv => kv.Value).Key;
            result.Cells.Add(
                new MapV2CellSummary
                {
                    Col = key.col,
                    Row = key.row,
                    SystemCount = count,
                    Region = dominantRegion,
                }
            );
        }

        // Compute actual grid bounds from data
        if (cellCounts.Count > 0)
        {
            var cols = cellCounts.Keys.Select(k => k.col).ToList();
            var rows = cellCounts.Keys.Select(k => k.row).ToList();
            result.GridStartCol = cols.Min();
            result.GridStartRow = rows.Min();
            result.GridColumns = cols.Max() - cols.Min() + 1;
            result.GridRows = rows.Max() - rows.Min() + 1;
        }

        // Nebulas — parse multi-grid values like "S-5 and S-6", "T-9/T-10", "E-9, F-9"
        var nebFilter = InfoboxDataFilter(KgNodeTypes.Nebula, InfoboxFieldLabels.GridSquare);
        if (continuity.HasValue)
            nebFilter = Builders<Page>.Filter.And(nebFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var nebRecs = await _pages.Find(nebFilter).ToListAsync();
        foreach (var rec in nebRecs)
        {
            var gridValues = GetDataValues(rec, InfoboxFieldLabels.GridSquare);
            var cells = new List<(int col, int row)>();
            foreach (var raw in gridValues)
            {
                // Split on common separators: "/", " and ", "&", ","
                var parts = Regex.Split(raw, @"[/,&]|\band\b", RegexOptions.IgnoreCase);
                foreach (var part in parts)
                {
                    if (TryParseGridSquare(part.Trim(), out var c, out var r))
                        cells.Add((c, r));
                }
            }
            if (cells.Count == 0)
                continue;
            result.Nebulas.Add(
                new MapV2Nebula
                {
                    Id = rec.PageId,
                    Name = rec.Title,
                    Col = cells[0].col,
                    Row = cells[0].row,
                    Cells = cells.Select(c => new[] { c.col, c.row }).ToList(),
                    Region = GetFirstDataValue(rec, InfoboxFieldLabels.Region) is { } rn ? NormalizeRegionName(rn) : null,
                }
            );
        }

        // Trade routes — resolve waypoints via the name→grid lookup
        var trFilter = TemplateFilter(KgNodeTypes.TradeRoute);
        if (continuity.HasValue)
            trFilter = Builders<Page>.Filter.And(trFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var trRecs = await _pages.Find(trFilter).ToListAsync();
        foreach (var rec in trRecs)
        {
            var endpoints = GetDataValues(rec, "End points");
            var otherObjects = GetDataValues(rec, "Other objects");

            var waypointNames = new List<string>();
            foreach (var obj in otherObjects)
            {
                var parts = obj.Split([" - ", " – "], StringSplitOptions.RemoveEmptyEntries);
                waypointNames.AddRange(parts.Select(p => p.Trim()));
            }
            if (waypointNames.Count == 0)
                waypointNames.AddRange(endpoints);

            var resolved = new List<MapV2Waypoint>();
            foreach (var name in waypointNames)
            {
                if (nameToGrid.TryGetValue(name, out var grid))
                {
                    if (resolved.Count > 0 && resolved[^1].Col == grid.col && resolved[^1].Row == grid.row)
                        continue;
                    resolved.Add(
                        new MapV2Waypoint
                        {
                            Name = name,
                            Col = grid.col,
                            Row = grid.row,
                        }
                    );
                }
            }
            if (resolved.Count >= 2)
                result.TradeRoutes.Add(
                    new MapV2TradeRoute
                    {
                        Id = rec.PageId,
                        Name = rec.Title,
                        Waypoints = resolved,
                    }
                );
        }

        _logger.LogInformation("GalaxyMap V2 overview: {Regions} regions, {Routes} trade routes, {Nebulas} nebulas", result.Regions.Count, result.TradeRoutes.Count, result.Nebulas.Count);

        return result;
    }

    /// <summary>
    /// Returns systems (with planets) within a grid range. Called on-demand as user zooms in.
    /// </summary>
    public async Task<GalaxyMapV2Systems> GetSystemsInRangeAsync(int minCol, int maxCol, int minRow, int maxRow, Continuity? continuity = null)
    {
        var sysFilter = InfoboxDataFilter(KgNodeTypes.System, InfoboxFieldLabels.GridSquare);
        if (continuity.HasValue)
            sysFilter = Builders<Page>.Filter.And(sysFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));
        var sysRecs = await _pages.Find(sysFilter).ToListAsync();

        // Build a name→(id, class) lookup for all celestial bodies to resolve planet IDs
        var cbLookup = new Dictionary<string, (int id, string? cls)>(StringComparer.OrdinalIgnoreCase);
        var cbRecs = await _pages
            .Find(TemplateFilter(KgNodeTypes.CelestialBody))
            .Project<Page>(Builders<Page>.Projection.Include(p => p.PageId).Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync();
        foreach (var cb in cbRecs)
        {
            var cls = GetFirstDataValue(cb, InfoboxFieldLabels.Class);
            cbLookup.TryAdd(cb.Title, (cb.PageId, cls));
        }

        var systems = new List<MapV2System>();
        foreach (var rec in sysRecs)
        {
            var gridSquare = GetFirstDataValue(rec, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridSquare, out var col, out var row))
                continue;
            if (col < minCol || col > maxCol || row < minRow || row > maxRow)
                continue;

            // Collect all orbital body names from all relevant properties
            var bodyNames = new List<string>();
            bodyNames.AddRange(GetDataValues(rec, "Orbiting bodies"));
            bodyNames.AddRange(GetDataValues(rec, "Planets"));
            bodyNames.AddRange(GetDataValues(rec, "Moons"));
            bodyNames.AddRange(GetDataValues(rec, "Asteroids"));
            bodyNames.AddRange(GetDataValues(rec, "Other objects"));
            var distinctNames = bodyNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var celestialBodies = distinctNames
                .Select(n =>
                {
                    cbLookup.TryGetValue(n, out var info);
                    return new MapV2CelestialBody
                    {
                        Id = info.id,
                        Name = n,
                        Class = info.cls,
                    };
                })
                .ToList();

            systems.Add(
                new MapV2System
                {
                    Id = rec.PageId,
                    Name = rec.Title,
                    Col = col,
                    Row = row,
                    Region = GetFirstDataValue(rec, InfoboxFieldLabels.Region) is { } rn ? NormalizeRegionName(rn) : null,
                    Sector = GetFirstDataValue(rec, InfoboxFieldLabels.Sector),
                    CelestialBodies = celestialBodies,
                }
            );
        }

        _logger.LogInformation("GalaxyMap V2 systems [{MinCol},{MinRow}]-[{MaxCol},{MaxRow}]: {Count} systems", minCol, minRow, maxCol, maxRow, systems.Count);

        return new GalaxyMapV2Systems
        {
            MinCol = minCol,
            MaxCol = maxCol,
            MinRow = minRow,
            MaxRow = maxRow,
            Systems = systems,
        };
    }
}
