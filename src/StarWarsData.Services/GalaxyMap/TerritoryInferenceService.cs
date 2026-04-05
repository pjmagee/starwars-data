using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// ETL Phase 8: Pre-computes complete territory year documents from the temporal knowledge graph.
/// Each year document contains everything the frontend needs — zero dynamic queries at request time.
/// Uses kg.nodes (Government lifecycles, Battle events) + kg.edges (affiliated_with, in_region).
/// </summary>
public class TerritoryInferenceService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<TerritoryInferenceService> logger)
{
    readonly IMongoCollection<GraphNode> _nodes = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);
    readonly IMongoCollection<TerritorySnapshot> _snapshots = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<TerritorySnapshot>(Collections.TerritorySnapshots);
    readonly IMongoCollection<TerritoryYearDocument> _yearDocs = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<TerritoryYearDocument>(Collections.TerritoryYears);
    readonly IMongoCollection<Page> _pages = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<Page>(Collections.Pages);
    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);

    public async Task InferTerritoryAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Territory inference: starting...");

        // ── Step 1: Load data from knowledge graph ──

        var governments = await _nodes
            .Find(Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.Government), Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        var govWithDates = governments.Where(g => g.StartYear.HasValue).ToList();

        // Planet → region from in_region edges
        var planetToRegion = new Dictionary<int, string>();
        var regionEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.InRegion), Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon))
            )
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync(ct);
        foreach (var edge in regionEdges)
        {
            var region = GalacticRegions.Normalise(edge[RelationshipEdgeBsonFields.ToName].AsString);
            if (region is not null)
                planetToRegion.TryAdd(edge[RelationshipEdgeBsonFields.FromId].AsInt32, region);
        }

        // Faction → planets per region from affiliated_with edges
        var factionRegionPlanets = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var affiliationEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, KgLabels.AffiliatedWith),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)
                )
            )
            .ToListAsync(ct);
        foreach (var edge in affiliationEdges)
        {
            if (!planetToRegion.TryGetValue(edge.FromId, out var region))
                continue;
            if (!factionRegionPlanets.TryGetValue(edge.ToName, out var regionCounts))
                factionRegionPlanets[edge.ToName] = regionCounts = new();
            regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
        }

        logger.LogInformation("Territory inference: {Govs} govs, {Planets} planet-regions, {Factions} factions", govWithDates.Count, planetToRegion.Count, factionRegionPlanets.Count);

        // ── Step 2: Load ALL event nodes (for key events per year) ──

        var eventNodes = await _nodes
            .Find(
                Builders<GraphNode>.Filter.And(
                    Builders<GraphNode>.Filter.In(n => n.Type, KgNodeTypes.EventLike),
                    Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon),
                    Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)
                )
            )
            .ToListAsync(ct);

        // Group events by year for fast lookup
        var eventsByYear = eventNodes.GroupBy(n => n.StartYear!.Value).ToDictionary(g => g.Key, g => g.ToList());

        logger.LogInformation("Territory inference: {Count} Canon event nodes with dates", eventNodes.Count);

        // ── Step 3: Load eras from Era nodes ──

        var eraNodes = await _nodes
            .Find(Builders<GraphNode>.Filter.And(Builders<GraphNode>.Filter.Eq(n => n.Type, KgNodeTypes.Era), Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)))
            .ToListAsync(ct);

        var eras = eraNodes
            .Where(e => e.StartYear.HasValue)
            .Select(e => new TerritoryEra
            {
                Name = e.Name,
                StartYear = e.StartYear!.Value,
                EndYear = e.EndYear ?? e.StartYear!.Value,
                Continuity = e.Continuity,
            })
            .OrderBy(e => e.StartYear)
            .ToList();

        // ── Step 4: Collect event years ──

        var eventYears = new SortedSet<int>();
        foreach (var gov in govWithDates)
        {
            if (gov.StartYear.HasValue)
                eventYears.Add(gov.StartYear.Value);
            if (gov.EndYear.HasValue)
                eventYears.Add(gov.EndYear.Value);
        }
        foreach (var yr in eventsByYear.Keys)
            eventYears.Add(yr);

        if (eventYears.Count == 0)
        {
            logger.LogWarning("No event years");
            return;
        }
        logger.LogInformation("Territory inference: {Count} event years", eventYears.Count);

        // ── Step 5: Build faction metadata (with wiki URLs and icons from Pages) ──

        var allFactionNames = factionRegionPlanets.Keys.ToList();
        var factionPages = await _pages
            .Find(Builders<Page>.Filter.And(Builders<Page>.Filter.In(p => p.Title, allFactionNames), Builders<Page>.Filter.Eq(p => p.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        var factionPageMap = factionPages.ToDictionary(p => p.Title, p => p, StringComparer.OrdinalIgnoreCase);

        var factionInfoList = allFactionNames
            .Where(f => factionRegionPlanets[f].Values.Sum() >= 3) // at least 3 affiliated planets
            .Select(f =>
            {
                factionPageMap.TryGetValue(f, out var page);
                return new TerritoryFactionInfo
                {
                    Name = f,
                    Color = FactionColorPalette.GetColor(f),
                    WikiUrl = page?.WikiUrl,
                    IconUrl = page?.Infobox?.ImageUrl,
                };
            })
            .OrderByDescending(f => factionRegionPlanets.GetValueOrDefault(f.Name)?.Values.Sum() ?? 0)
            .ToList();

        // ── Step 5b: Build planet → grid coordinate lookup from Pages infobox ──

        var planetToGrid = new Dictionary<int, (int col, int row)>();
        var gridFilter = MongoDB.Bson.BsonDocument.Parse("{ 'infobox.Data': { $elemMatch: { Label: 'Grid square' } } }");
        var gridPages = await _pages.Find(gridFilter).Project(Builders<Page>.Projection.Include(p => p.PageId).Include("infobox.Data")).ToListAsync(ct);

        foreach (var doc in gridPages)
        {
            var pageId = doc[MongoFields.Id].AsInt32;
            var data = doc.Contains(PageBsonFields.Infobox) && doc[PageBsonFields.Infobox].IsBsonDocument ? doc[PageBsonFields.Infobox].AsBsonDocument : null;
            if (data is null)
                continue;
            var dataArr = data.Contains(InfoboxBsonFields.Data) && data[InfoboxBsonFields.Data].IsBsonArray ? data[InfoboxBsonFields.Data].AsBsonArray : null;
            if (dataArr is null)
                continue;

            foreach (var item in dataArr.OfType<MongoDB.Bson.BsonDocument>())
            {
                if (item.GetValue(InfoboxBsonFields.Label, "").AsString != InfoboxFieldLabels.GridSquare)
                    continue;
                var vals = item.Contains(InfoboxBsonFields.Values) && item[InfoboxBsonFields.Values].IsBsonArray ? item[InfoboxBsonFields.Values].AsBsonArray : null;
                var gridStr = vals?.FirstOrDefault()?.AsString;
                if (gridStr is not null && TryParseGridSquare(gridStr, out var col, out var row))
                    planetToGrid.TryAdd(pageId, (col, row));
                break;
            }
        }

        logger.LogInformation("Territory inference: {Count} planets with grid coordinates", planetToGrid.Count);

        // ── Step 6: Build year documents ──

        var snapshots = new List<TerritorySnapshot>();
        var yearDocs = new List<TerritoryYearDocument>();

        // Build an adjacency list from ALL Canon edges for graph traversal
        var allCanonEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon))
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToId).Include(e => e.ToName).Include(e => e.Label))
            .ToListAsync(ct);

        var adjacency = new Dictionary<int, List<(int toId, string toName, string label)>>();
        foreach (var edge in allCanonEdges)
        {
            var fromId = edge[RelationshipEdgeBsonFields.FromId].AsInt32;
            var toId = edge[RelationshipEdgeBsonFields.ToId].AsInt32;
            var toName = edge[RelationshipEdgeBsonFields.ToName].AsString;
            var label = edge[RelationshipEdgeBsonFields.Label].AsString;
            if (!adjacency.TryGetValue(fromId, out var list))
                adjacency[fromId] = list = [];
            list.Add((toId, toName, label));
        }

        logger.LogInformation("Territory inference: built adjacency list from {Count} Canon edges", allCanonEdges.Count);

        foreach (var year in eventYears)
        {
            var activeGovs = govWithDates.Where(g => g.StartYear <= year && (!g.EndYear.HasValue || g.EndYear >= year)).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Region control
            var regionControls = new List<TerritoryRegionControl>();
            foreach (var region in GalacticRegions.All)
            {
                var regionFactions = factionRegionPlanets
                    .Where(kv => activeGovs.Contains(kv.Key))
                    .Where(kv => kv.Value.ContainsKey(region))
                    .Select(kv => (Faction: kv.Key, Count: kv.Value[region]))
                    .OrderByDescending(x => x.Count)
                    .ToList();

                if (regionFactions.Count == 0)
                {
                    var def = GetDefaultFaction(year);
                    if (def is not null && activeGovs.Contains(def))
                    {
                        regionControls.Add(
                            new TerritoryRegionControl
                            {
                                Region = region,
                                Factions =
                                [
                                    new()
                                    {
                                        Faction = def,
                                        Control = 1.0,
                                        Color = FactionColorPalette.GetColor(def),
                                    },
                                ],
                            }
                        );
                        snapshots.Add(
                            new()
                            {
                                Year = year,
                                Region = region,
                                Faction = def,
                                Control = 1.0,
                                Color = FactionColorPalette.GetColor(def),
                            }
                        );
                    }
                    continue;
                }

                var total = regionFactions.Sum(x => x.Count);
                var dominant = regionFactions[0];
                var control = (double)dominant.Count / total;
                var contested = regionFactions.Count > 1 && control < 0.7;

                var factions = new List<TerritoryFactionControl>
                {
                    new()
                    {
                        Faction = dominant.Faction,
                        Control = Math.Max(0.3, control),
                        Contested = contested,
                        Color = FactionColorPalette.GetColor(dominant.Faction),
                    },
                };
                snapshots.Add(
                    new()
                    {
                        Year = year,
                        Region = region,
                        Faction = dominant.Faction,
                        Control = Math.Max(0.3, control),
                        Contested = contested,
                        Color = FactionColorPalette.GetColor(dominant.Faction),
                    }
                );

                if (contested)
                {
                    foreach (var (f, c) in regionFactions.Skip(1))
                    {
                        var oc = Math.Max(0.1, (double)c / total);
                        factions.Add(
                            new()
                            {
                                Faction = f,
                                Control = oc,
                                Contested = true,
                                Color = FactionColorPalette.GetColor(f),
                            }
                        );
                        snapshots.Add(
                            new()
                            {
                                Year = year,
                                Region = region,
                                Faction = f,
                                Control = oc,
                                Contested = true,
                                Color = FactionColorPalette.GetColor(f),
                            }
                        );
                    }
                }

                regionControls.Add(new() { Region = region, Factions = factions });
            }

            // Key events from kg.nodes at this year
            var keyEvents = new List<TerritoryKeyEvent>();
            if (eventsByYear.TryGetValue(year, out var yearEvents))
            {
                foreach (var evt in yearEvents.Take(30))
                {
                    // Graph traversal: BFS from the event node up to 3 hops
                    // looking for any connected node with grid coordinates
                    var (place, region, col, row) = ResolveLocationByTraversal(evt.PageId, adjacency, planetToRegion, planetToGrid, maxDepth: 3);

                    var outcome = evt.Properties.GetValueOrDefault(InfoboxFieldLabels.Outcome)?.FirstOrDefault();

                    keyEvents.Add(
                        new TerritoryKeyEvent
                        {
                            Year = year,
                            Title = evt.Name,
                            Category = evt.Type,
                            WikiUrl = evt.WikiUrl,
                            Place = place,
                            Region = region,
                            Col = col,
                            Row = row,
                            Description = outcome,
                        }
                    );
                }
            }

            // Era for this year
            var era = eras.FirstOrDefault(e => year >= e.StartYear && year <= e.EndYear);

            yearDocs.Add(
                new TerritoryYearDocument
                {
                    Year = year,
                    YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
                    Era = era?.Name,
                    EraDescription = era?.Description,
                    Regions = regionControls,
                    KeyEvents = keyEvents,
                }
            );
        }

        // ── Step 7: Build overview document ──

        var overview = new TerritoryOverviewDocument
        {
            MinYear = eventYears.Min,
            MaxYear = eventYears.Max,
            AvailableYears = eventYears.ToList(),
            Factions = factionInfoList,
            Eras = eras,
            Regions = [.. GalacticRegions.All],
        };

        // ── Step 8: Write to MongoDB ──

        await _snapshots.DeleteManyAsync(FilterDefinition<TerritorySnapshot>.Empty, ct);
        await _yearDocs.DeleteManyAsync(FilterDefinition<TerritoryYearDocument>.Empty, ct);

        if (snapshots.Count > 0)
            await _snapshots.InsertManyAsync(snapshots, cancellationToken: ct);
        if (yearDocs.Count > 0)
            await _yearDocs.InsertManyAsync(yearDocs, cancellationToken: ct);

        // Store overview as a special document in the years collection
        var overviewColl = _db.GetCollection<TerritoryOverviewDocument>(Collections.TerritoryYears);
        await overviewColl.ReplaceOneAsync(Builders<TerritoryOverviewDocument>.Filter.Eq(o => o.Id, "overview"), overview, new ReplaceOptions { IsUpsert = true }, ct);

        await _yearDocs.Indexes.CreateOneAsync(new CreateIndexModel<TerritoryYearDocument>(Builders<TerritoryYearDocument>.IndexKeys.Ascending(d => d.Year)), cancellationToken: ct);

        logger.LogInformation("Territory inference: {Snapshots} snapshots, {Years} year docs, {Factions} factions, overview stored", snapshots.Count, yearDocs.Count, factionInfoList.Count);
    }

    static string? GetDefaultFaction(int year) =>
        year switch
        {
            <= -19 => "Galactic Republic",
            <= 4 => "Galactic Empire",
            <= 34 => "New Republic",
            <= 35 => "First Order",
            _ => null,
        };

    /// <summary>
    /// BFS from an entity node through the knowledge graph, looking for the nearest
    /// connected node that has grid coordinates. Traverses any edge type.
    /// </summary>
    static (string? place, string? region, int? col, int? row) ResolveLocationByTraversal(
        int entityId,
        Dictionary<int, List<(int toId, string toName, string label)>> adjacency,
        Dictionary<int, string> planetToRegion,
        Dictionary<int, (int col, int row)> planetToGrid,
        int maxDepth = 3
    )
    {
        string? place = null,
            region = null;
        int? col = null,
            row = null;

        var visited = new HashSet<int> { entityId };
        var frontier = new List<int> { entityId };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<int>();

            foreach (var nodeId in frontier)
            {
                if (!adjacency.TryGetValue(nodeId, out var neighbors))
                    continue;

                foreach (var (toId, toName, label) in neighbors)
                {
                    if (toId <= 0 || !visited.Add(toId))
                        continue;

                    // Check if this neighbor has grid coordinates
                    if (planetToGrid.TryGetValue(toId, out var grid))
                    {
                        col = grid.col;
                        row = grid.row;
                        place = toName;
                        if (planetToRegion.TryGetValue(toId, out var r))
                            region = r;
                        return (place, region, col, row);
                    }

                    // Check region even without grid
                    if (region is null && planetToRegion.TryGetValue(toId, out var rg))
                    {
                        region = rg;
                        place ??= toName;
                    }

                    nextFrontier.Add(toId);
                }
            }

            frontier = nextFrontier;
        }

        return (place, region, col, row);
    }

    static bool TryParseGridSquare(string gridSquare, out int col, out int row)
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
}
