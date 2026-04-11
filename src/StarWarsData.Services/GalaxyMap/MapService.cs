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
    readonly IMongoCollection<GraphNode> _nodes;
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly SemanticSearchService _semanticSearch;

    public MapService(ILogger<MapService> logger, IOptions<SettingsOptions> settingsOptions, IMongoClient mongoClient, SemanticSearchService semanticSearch)
    {
        _logger = logger;
        var db = mongoClient.GetDatabase(settingsOptions.Value.DatabaseName);
        _pages = db.GetCollection<Page>(Collections.Pages);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _semanticSearch = semanticSearch;
    }

    // ── Raw.pages helpers (used only by search methods) ──

    static FilterDefinition<Page> TemplateFilter(string type) => Builders<Page>.Filter.Eq("infobox.Template", $"{Collections.TemplateUrlPrefix}{type}");

    static FilterDefinition<Page> InfoboxDataFilter(string type, string label) =>
        Builders<Page>.Filter.And(TemplateFilter(type), Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, label)));

    static List<string> GetDataValues(Page page, string label) => page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values ?? [];

    static string? GetFirstDataValue(Page page, string label) => page.Infobox?.Data.FirstOrDefault(d => d.Label == label)?.Values.FirstOrDefault();

    /// <summary>
    /// Normalize region names to merge duplicates in the wiki data.
    /// </summary>
    internal static string NormalizeRegionName(string region)
    {
        var sepIdx = region.IndexOfAny([';', ',']);
        if (sepIdx > 0)
            region = region[..sepIdx].Trim();

        region = region.Trim();

        if (region.EndsWith(" Territories", StringComparison.OrdinalIgnoreCase))
            region = region[..^" Territories".Length];

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

    // Labels on non-spatial entities that reference locations (used by search)
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

    // ── KG helpers ──

    static string GetNodeProperty(GraphNode node, string key) => node.Properties.TryGetValue(key, out var vals) ? vals.FirstOrDefault() ?? "" : "";

    static List<string> GetNodeProperties(GraphNode node, string key) => node.Properties.TryGetValue(key, out var vals) ? vals : [];

    /// <summary>
    /// Get the display name for a KG node. Uses the Titles property (clean infobox title)
    /// falling back to the node name.
    /// </summary>
    static string GetDisplayName(GraphNode node)
    {
        var titles = GetNodeProperties(node, InfoboxFieldLabels.Titles);
        return titles.Count > 0 ? titles[0] : node.Name;
    }

    // ══════════ SEARCH (still uses raw.pages for text search) ══════════

    public async Task<List<MapSearchResult>> SearchGridAsync(string term, Continuity? continuity = null)
    {
        term = MongoSafe.Sanitize(term);
        var escaped = Regex.Escape(term);
        var results = new List<MapSearchResult>();
        var seen = new HashSet<string>();

        // 1. Direct matches: entities WITH grid squares whose title or content matches
        var directTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))
        );
        if (continuity.HasValue)
            directTextFilter = Builders<Page>.Filter.And(directTextFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));

        var directMatches = await _pages.Find(directTextFilter).Sort(Builders<Page>.Sort.MetaTextScore("score")).Limit(50).ToListAsync();

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

        // 2. Indirect matches: entities WITHOUT grid squares — follow location references
        var indirectTextFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Text(term),
            Builders<Page>.Filter.Not(Builders<Page>.Filter.ElemMatch<BsonDocument>("infobox.Data", new BsonDocument(InfoboxBsonFields.Label, InfoboxFieldLabels.GridSquare))),
            Builders<Page>.Filter.Ne(PageBsonFields.Infobox, BsonNull.Value)
        );
        if (continuity.HasValue)
            indirectTextFilter = Builders<Page>.Filter.And(indirectTextFilter, Builders<Page>.Filter.Eq(r => r.Continuity, continuity.Value));

        var indirectMatches = await _pages.Find(indirectTextFilter).Sort(Builders<Page>.Sort.MetaTextScore("score")).Limit(50).ToListAsync();

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

    public async Task<List<MapSearchResult>> SemanticSearchGridAsync(string query, Continuity? continuity = null, int limit = 10)
    {
        const double minScore = 0.6;
        var spatialResults = await _semanticSearch.SearchAsync(query, [KgNodeTypes.System, KgNodeTypes.CelestialBody], continuity, limit: limit * 2, minScore: minScore);
        var allResults = await _semanticSearch.SearchAsync(query, null, continuity, limit: limit * 3, minScore: minScore);

        var pageScores = new Dictionary<int, SearchHit>();
        foreach (var r in spatialResults.Concat(allResults))
            pageScores.TryAdd(r.PageId, r);

        if (pageScores.Count == 0)
            return [];

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
            results.Add(
                new MapSearchResult
                {
                    GridKey = $"{(char)('A' + col)}-{row + 1}",
                    PageId = page.PageId,
                    MatchedName = page.Title,
                    Template = page.Infobox?.Template?.Split(':').LastOrDefault(),
                    MatchType = "semantic",
                    Snippet = info.Text.Length > 200 ? info.Text[..200] + "..." : info.Text,
                    Score = info.Score,
                }
            );
        }

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
                            Snippet = info.Text.Length > 200 ? info.Text[..200] + "..." : info.Text,
                            Score = info.Score,
                        }
                    );
                }
            }
        }

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

    // ══════════ GEOGRAPHY (KG-backed) ══════════

    /// <summary>
    /// Lightweight overview: regions, trade routes, nebulas, cell summaries. No systems.
    /// All data sourced from kg.nodes + kg.edges.
    /// </summary>
    public async Task<GalaxyGeography> GetGeographyAsync(Continuity? continuity = null)
    {
        var result = new GalaxyGeography();

        // ── Load all System nodes with grid squares ──
        var sysFilter = Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.System), Builders<GraphNode>.Filter.Exists("properties.Grid square"));
        if (continuity.HasValue)
            sysFilter = Builders<GraphNode>.Filter.And(sysFilter, Builders<GraphNode>.Filter.Eq(n => n.Continuity, continuity.Value));

        var systemNodes = await _nodes
            .Find(sysFilter)
            .Project(Builders<GraphNode>.Projection.Include(n => n.PageId).Include(n => n.Name).Include("properties.Grid square").Include("properties.Titles"))
            .ToListAsync();

        // ── Load edges for region/sector resolution ──
        var systemIds = systemNodes.Select(n => n[MongoFields.Id].AsInt32).ToList();

        var regionEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.InRegion),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, KgNodeTypes.System),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, KgNodeTypes.Region)
                )
            )
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync();

        var sectorEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.InSector),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, KgNodeTypes.System),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, KgNodeTypes.Sector)
                )
            )
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync();

        // Build lookups: systemId → region, systemId → sector
        var systemToRegion = new Dictionary<int, string>();
        foreach (var e in regionEdges)
        {
            var id = e[RelationshipEdgeBsonFields.FromId].AsInt32;
            var region = NormalizeRegionName(e[RelationshipEdgeBsonFields.ToName].AsString);
            systemToRegion.TryAdd(id, region);
        }

        var systemToSector = new Dictionary<int, string>();
        foreach (var e in sectorEdges)
        {
            var id = e[RelationshipEdgeBsonFields.FromId].AsInt32;
            systemToSector.TryAdd(id, e[RelationshipEdgeBsonFields.ToName].AsString);
        }

        // ── Build spatial lookups ──
        var nameToGrid = new Dictionary<string, (int col, int row)>(StringComparer.OrdinalIgnoreCase);
        var pageIdToGrid = new Dictionary<int, (int col, int row)>();
        var pageIdToName = new Dictionary<int, string>();
        var regionCells = new Dictionary<string, HashSet<(int col, int row)>>(StringComparer.OrdinalIgnoreCase);
        var cellCounts = new Dictionary<(int col, int row), int>();
        var cellRegion = new Dictionary<(int col, int row), Dictionary<string, int>>();
        var cellSectors = new Dictionary<(int col, int row), Dictionary<string, int>>();

        foreach (var doc in systemNodes)
        {
            var pageId = doc[MongoFields.Id].AsInt32;
            var name = doc[GraphNodeBsonFields.Name].AsString;
            var gridArr = doc["properties"]["Grid square"].AsBsonArray;
            var gridStr = gridArr.Count > 0 ? gridArr[0].AsString : null;
            if (!TryParseGridSquare(gridStr, out var col, out var row))
                continue;

            nameToGrid.TryAdd(name, (col, row));
            pageIdToGrid.TryAdd(pageId, (col, row));
            pageIdToName.TryAdd(pageId, name);
            if (doc["properties"].AsBsonDocument.Contains("Titles"))
            {
                var titles = doc["properties"]["Titles"].AsBsonArray;
                foreach (var t in titles)
                    nameToGrid.TryAdd(t.AsString, (col, row));
            }

            var key = (col, row);
            cellCounts[key] = cellCounts.GetValueOrDefault(key) + 1;

            if (systemToRegion.TryGetValue(pageId, out var region))
            {
                if (!regionCells.TryGetValue(region, out var cells))
                    regionCells[region] = cells = [];
                cells.Add(key);

                if (!cellRegion.TryGetValue(key, out var regionCount))
                    cellRegion[key] = regionCount = new(StringComparer.OrdinalIgnoreCase);
                regionCount[region] = regionCount.GetValueOrDefault(region) + 1;
            }

            if (systemToSector.TryGetValue(pageId, out var sector))
            {
                if (!cellSectors.TryGetValue(key, out var sectorCount))
                    cellSectors[key] = sectorCount = new(StringComparer.OrdinalIgnoreCase);
                sectorCount[sector] = sectorCount.GetValueOrDefault(sector) + 1;
            }
        }

        // ── Also index CelestialBody nodes with grid squares (for trade route resolution) ──
        var cbFilter = Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.CelestialBody), Builders<GraphNode>.Filter.Exists("properties.Grid square"));
        if (continuity.HasValue)
            cbFilter = Builders<GraphNode>.Filter.And(cbFilter, Builders<GraphNode>.Filter.Eq(n => n.Continuity, continuity.Value));

        var cbNodes = await _nodes
            .Find(cbFilter)
            .Project(Builders<GraphNode>.Projection.Include(n => n.PageId).Include(n => n.Name).Include("properties.Grid square").Include("properties.Titles"))
            .ToListAsync();

        foreach (var doc in cbNodes)
        {
            var name = doc[GraphNodeBsonFields.Name].AsString;
            var gridArr = doc["properties"]["Grid square"].AsBsonArray;
            var gridStr = gridArr.Count > 0 ? gridArr[0].AsString : null;
            if (!TryParseGridSquare(gridStr, out var col, out var row))
                continue;
            nameToGrid.TryAdd(name, (col, row));
            var cbPageId = doc[MongoFields.Id].AsInt32;
            pageIdToGrid.TryAdd(cbPageId, (col, row));
            pageIdToName.TryAdd(cbPageId, name);
            if (doc["properties"].AsBsonDocument.Contains("Titles"))
            {
                var titles = doc["properties"]["Titles"].AsBsonArray;
                foreach (var t in titles)
                    nameToGrid.TryAdd(t.AsString, (col, row));
            }

            if (systemToRegion.TryGetValue(cbPageId, out var region))
            {
                if (!regionCells.TryGetValue(region, out var cells))
                    regionCells[region] = cells = [];
                cells.Add((col, row));
            }
        }

        // ── Build regions ──
        foreach (var (name, cells) in regionCells)
            result.Regions.Add(new GeoRegion { Name = name, Cells = cells.Select(c => new[] { c.col, c.row }).ToList() });

        // ── Build cell summaries ──
        foreach (var (key, count) in cellCounts)
        {
            string? dominantRegion = null;
            if (cellRegion.TryGetValue(key, out var regionCount))
                dominantRegion = regionCount.MaxBy(kv => kv.Value).Key;

            List<GeoCellSector>? sectors = null;
            if (cellSectors.TryGetValue(key, out var sectorCounts) && sectorCounts.Count > 0)
                sectors = sectorCounts.OrderByDescending(kv => kv.Value).Select(kv => new GeoCellSector { Name = kv.Key, Count = kv.Value }).ToList();

            result.Cells.Add(
                new GeoCellSummary
                {
                    Col = key.col,
                    Row = key.row,
                    SystemCount = count,
                    Region = dominantRegion,
                    Sectors = sectors,
                }
            );
        }

        // ── Compute grid bounds ──
        if (cellCounts.Count > 0)
        {
            var cols = cellCounts.Keys.Select(k => k.col).ToList();
            var rows = cellCounts.Keys.Select(k => k.row).ToList();
            result.GridStartCol = cols.Min();
            result.GridStartRow = rows.Min();
            result.GridColumns = cols.Max() - cols.Min() + 1;
            result.GridRows = rows.Max() - rows.Min() + 1;
        }

        // ── Nebulas from KG ──
        var nebFilter = Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.Nebula), Builders<GraphNode>.Filter.Exists("properties.Grid square"));
        if (continuity.HasValue)
            nebFilter = Builders<GraphNode>.Filter.And(nebFilter, Builders<GraphNode>.Filter.Eq(n => n.Continuity, continuity.Value));

        var nebNodes = await _nodes.Find(nebFilter).ToListAsync();
        foreach (var neb in nebNodes)
        {
            var gridVals = GetNodeProperties(neb, InfoboxFieldLabels.GridSquare);
            var cells = new List<(int col, int row)>();
            foreach (var raw in gridVals)
            {
                var parts = Regex.Split(raw, @"[/,&]|\band\b", RegexOptions.IgnoreCase);
                foreach (var part in parts)
                    if (TryParseGridSquare(part.Trim(), out var c, out var r))
                        cells.Add((c, r));
            }
            if (cells.Count == 0)
                continue;
            var regionVal = GetNodeProperty(neb, InfoboxFieldLabels.Region);
            result.Nebulas.Add(
                new GeoNebula
                {
                    Id = neb.PageId,
                    Name = GetDisplayName(neb),
                    Col = cells[0].col,
                    Row = cells[0].row,
                    Cells = cells.Select(c => new[] { c.col, c.row }).ToList(),
                    Region = !string.IsNullOrEmpty(regionVal) ? NormalizeRegionName(regionVal) : null,
                }
            );
        }

        // ── Trade routes from KG nodes (ordered pageId sequences in properties) ──
        var trNodeFilter = Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.TradeRoute);
        if (continuity.HasValue)
            trNodeFilter = Builders<GraphNode>.Filter.And(trNodeFilter, Builders<GraphNode>.Filter.Eq(n => n.Continuity, continuity.Value));

        var tradeRouteNodes = await _nodes.Find(trNodeFilter).ToListAsync();
        foreach (var trNode in tradeRouteNodes)
        {
            // Ordered pageId sequences stored by the ETL as "{field}Ids" properties.
            // Prefer "Other objectsIds" (full waypoint sequence), then "Transit pointsIds", then "End pointsIds".
            var waypointIds = GetNodeProperties(trNode, "Other objectsIds");
            if (waypointIds.Count == 0)
                waypointIds = GetNodeProperties(trNode, "Transit pointsIds");
            if (waypointIds.Count == 0)
                waypointIds = GetNodeProperties(trNode, "End pointsIds");

            // Resolve each pageId to grid coordinates in order
            var waypoints = new List<GeoWaypoint>();
            foreach (var idStr in waypointIds)
            {
                if (!int.TryParse(idStr, out var wpId))
                    continue;
                if (!pageIdToGrid.TryGetValue(wpId, out var grid))
                    continue;
                if (waypoints.Count > 0 && waypoints[^1].Col == grid.col && waypoints[^1].Row == grid.row)
                    continue;
                var wpName = pageIdToName.GetValueOrDefault(wpId, wpId.ToString());
                waypoints.Add(
                    new GeoWaypoint
                    {
                        Name = wpName,
                        Col = grid.col,
                        Row = grid.row,
                    }
                );
            }

            if (waypoints.Count >= 2)
                result.TradeRoutes.Add(
                    new GeoTradeRoute
                    {
                        Id = trNode.PageId,
                        Name = GetDisplayName(trNode),
                        Waypoints = waypoints,
                    }
                );
        }

        _logger.LogInformation(
            "GalaxyMap geography (KG): {Regions} regions, {Routes} trade routes, {Nebulas} nebulas, {Cells} cells",
            result.Regions.Count,
            result.TradeRoutes.Count,
            result.Nebulas.Count,
            result.Cells.Count
        );

        return result;
    }

    // ══════════ SYSTEMS (KG-backed) ══════════

    /// <summary>
    /// Returns systems (with celestial bodies) within a grid range.
    /// All data sourced from kg.nodes + kg.edges.
    /// </summary>
    public async Task<GalaxyGeographySystems> GetSystemsInRangeAsync(int minCol, int maxCol, int minRow, int maxRow, Continuity? continuity = null)
    {
        // Load system nodes with grid squares
        var sysFilter = Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.System), Builders<GraphNode>.Filter.Exists("properties.Grid square"));
        if (continuity.HasValue)
            sysFilter = Builders<GraphNode>.Filter.And(sysFilter, Builders<GraphNode>.Filter.Eq(n => n.Continuity, continuity.Value));

        var systemNodes = await _nodes.Find(sysFilter).ToListAsync();

        // Filter to viewport
        var inRange = new List<GraphNode>();
        foreach (var node in systemNodes)
        {
            var gridStr = GetNodeProperty(node, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridStr, out var col, out var row))
                continue;
            if (col >= minCol && col <= maxCol && row >= minRow && row <= maxRow)
                inRange.Add(node);
        }

        // Load region + sector edges for these systems
        var inRangeIds = inRange.Select(n => n.PageId).ToHashSet();

        var regionEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.InRegion),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, KgNodeTypes.System),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, KgNodeTypes.Region)
                )
            )
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync();

        var sectorEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.InSector),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, KgNodeTypes.System),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, KgNodeTypes.Sector)
                )
            )
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync();

        var systemRegion = new Dictionary<int, string>();
        foreach (var e in regionEdges)
        {
            var id = e[RelationshipEdgeBsonFields.FromId].AsInt32;
            if (inRangeIds.Contains(id))
                systemRegion.TryAdd(id, NormalizeRegionName(e[RelationshipEdgeBsonFields.ToName].AsString));
        }

        var systemSector = new Dictionary<int, string>();
        foreach (var e in sectorEdges)
        {
            var id = e[RelationshipEdgeBsonFields.FromId].AsInt32;
            if (inRangeIds.Contains(id))
                systemSector.TryAdd(id, e[RelationshipEdgeBsonFields.ToName].AsString);
        }

        // Load orbited_by edges for celestial bodies
        var orbitEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(Builders<RelationshipEdge>.Filter.In(e => e.FromId, inRangeIds), Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.OrbitedBy)))
            .ToListAsync();

        // Group orbited_by by system
        var systemBodies = new Dictionary<int, List<(int toId, string toName)>>();
        foreach (var e in orbitEdges)
        {
            if (!systemBodies.TryGetValue(e.FromId, out var list))
                systemBodies[e.FromId] = list = [];
            list.Add((e.ToId, e.ToName));
        }

        // Load celestial body nodes for class + continuity
        var allBodyIds = systemBodies.Values.SelectMany(l => l.Select(b => b.toId)).Distinct().ToList();
        var bodyNodes =
            allBodyIds.Count > 0
                ? await _nodes
                    .Find(Builders<GraphNode>.Filter.In(n => n.PageId, allBodyIds))
                    .Project(Builders<GraphNode>.Projection.Include(n => n.PageId).Include(n => n.Name).Include(n => n.Continuity).Include("properties.Class").Include("properties.Titles"))
                    .ToListAsync()
                : [];

        var bodyLookup = bodyNodes.ToDictionary(
            d => d[MongoFields.Id].AsInt32,
            d =>
            {
                var cls = d["properties"].AsBsonDocument.Contains("Class") && d["properties"]["Class"].AsBsonArray.Count > 0 ? d["properties"]["Class"][0].AsString : null;
                var cont = d.Contains(GraphNodeBsonFields.Continuity) ? d[GraphNodeBsonFields.Continuity].AsString : "Unknown";
                var name =
                    d["properties"].AsBsonDocument.Contains("Titles") && d["properties"]["Titles"].AsBsonArray.Count > 0 ? d["properties"]["Titles"][0].AsString : d[GraphNodeBsonFields.Name].AsString;
                return (name, cls, cont);
            }
        );

        // Build result
        var systems = new List<GeoSystem>();
        foreach (var node in inRange)
        {
            var gridStr = GetNodeProperty(node, InfoboxFieldLabels.GridSquare);
            if (!TryParseGridSquare(gridStr, out var col, out var row))
                continue;

            var celestialBodies = new List<GeoCelestialBody>();
            if (systemBodies.TryGetValue(node.PageId, out var bodies))
            {
                foreach (var (toId, toName) in bodies)
                {
                    if (bodyLookup.TryGetValue(toId, out var info))
                        celestialBodies.Add(
                            new GeoCelestialBody
                            {
                                Id = toId,
                                Name = info.name,
                                Class = info.cls,
                                Continuity = info.cont,
                            }
                        );
                    else
                        celestialBodies.Add(new GeoCelestialBody { Id = toId, Name = toName });
                }
            }

            systems.Add(
                new GeoSystem
                {
                    Id = node.PageId,
                    Name = GetDisplayName(node),
                    Col = col,
                    Row = row,
                    Continuity = node.Continuity.ToString(),
                    Region = systemRegion.GetValueOrDefault(node.PageId),
                    Sector = systemSector.GetValueOrDefault(node.PageId),
                    CelestialBodies = celestialBodies,
                }
            );
        }

        _logger.LogInformation("GalaxyMap systems (KG) [{MinCol},{MinRow}]-[{MaxCol},{MaxRow}]: {Count} systems", minCol, minRow, maxCol, maxRow, systems.Count);

        return new GalaxyGeographySystems
        {
            MinCol = minCol,
            MaxCol = maxCol,
            MinRow = minRow,
            MaxRow = maxRow,
            Systems = systems,
        };
    }
}
