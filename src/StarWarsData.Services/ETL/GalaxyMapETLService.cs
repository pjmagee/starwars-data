using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// ETL Phase 9: Builds the unified galaxy.years collection.
/// Pre-computes territory control + event heatmap data per year.
/// Uses kg.nodes + kg.edges for territory and BFS location resolution,
/// plus timeline.* collections for the full event corpus.
/// </summary>
public class GalaxyMapETLService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> settings,
    ILogger<GalaxyMapETLService> logger
)
{
    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);
    readonly IMongoCollection<GraphNode> _nodes = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<GraphNode>(Collections.KgNodes);
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<RelationshipEdge>(Collections.KgEdges);
    readonly IMongoCollection<Page> _pages = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<Page>(Collections.Pages);

    static readonly Dictionary<string, string> FactionColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Galactic Republic"] = "#3b82f6",
        ["Confederacy of Independent Systems"] = "#ef4444",
        ["Galactic Empire"] = "#6b7280",
        ["Alliance to Restore the Republic"] = "#f97316",
        ["New Republic"] = "#22c55e",
        ["First Order"] = "#991b1b",
        ["Resistance"] = "#f59e0b",
        ["Hutt Clan"] = "#a8722a",
        ["Chiss Ascendancy"] = "#1e3a5f",
        ["Nihil"] = "#8b5cf6",
    };

    static readonly string[] ColorPalette =
    [
        "#3b82f6",
        "#ef4444",
        "#6b7280",
        "#f97316",
        "#22c55e",
        "#991b1b",
        "#f59e0b",
        "#a8722a",
        "#1e3a5f",
        "#8b5cf6",
        "#ec4899",
        "#14b8a6",
        "#84cc16",
        "#d946ef",
        "#06b6d4",
    ];

    static readonly string[] GalacticRegions =
    [
        "Deep Core",
        "Core Worlds",
        "Colonies",
        "Inner Rim",
        "Expansion Region",
        "Mid Rim",
        "Outer Rim Territories",
        "Hutt Space",
        "Unknown Regions",
        "Wild Space",
    ];

    static readonly string[] EventNodeTypes =
    [
        "Battle",
        "War",
        "Campaign",
        "Government",
        "Treaty",
        "Event",
    ];

    static readonly string[] LocationLabels =
    [
        "Place",
        "Location",
        "System",
        "Headquarters",
        "Capital",
        "Grid square",
        "Celestial body",
    ];

    static readonly HashSet<string> SkipCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "system_profile",
        "system.profile",
    };

    // Out-of-Universe detection now uses node.Universe field from the KG
    // instead of a hardcoded list of type names. See lensOutOfUniverse population in BuildGalaxyMapAsync.

    public async Task BuildGalaxyMapAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Galaxy map ETL: starting...");

        // ── Step 1: Load territory data from knowledge graph ──

        var governments = await _nodes
            .Find(
                Builders<GraphNode>.Filter.And(
                    Builders<GraphNode>.Filter.Eq(n => n.Type, "Government"),
                    Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon)
                )
            )
            .ToListAsync(ct);
        var govWithDates = governments.Where(g => g.StartYear.HasValue).ToList();

        // Planet → region from in_region edges
        var planetToRegion = new Dictionary<int, string>();
        var regionEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "in_region"),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)
                )
            )
            .Project(
                Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName)
            )
            .ToListAsync(ct);
        foreach (var edge in regionEdges)
        {
            var region = NormaliseRegion(edge["toName"].AsString);
            if (region is not null)
                planetToRegion.TryAdd(edge["fromId"].AsInt32, region);
        }

        // Faction → planets per region from affiliated_with edges (with temporal bounds)
        var factionRegionPlanets = new Dictionary<string, Dictionary<string, int>>(
            StringComparer.OrdinalIgnoreCase
        );
        // Store raw affiliations with temporal bounds for per-year filtering
        var temporalAffiliations =
            new List<(string faction, string region, int? fromYear, int? toYear)>();

        var affiliationEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "affiliated_with"),
                    Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)
                )
            )
            .ToListAsync(ct);
        foreach (var edge in affiliationEdges)
        {
            if (!planetToRegion.TryGetValue(edge.FromId, out var region))
                continue;
            // Static totals (for faction discovery)
            if (!factionRegionPlanets.TryGetValue(edge.ToName, out var regionCounts))
                factionRegionPlanets[edge.ToName] = regionCounts = new();
            regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
            // Track temporal bounds for per-year filtering
            temporalAffiliations.Add((edge.ToName, region, edge.FromYear, edge.ToYear));
        }

        logger.LogInformation(
            "Galaxy map ETL: {Govs} govs, {Planets} planet-regions, {Factions} factions",
            govWithDates.Count,
            planetToRegion.Count,
            factionRegionPlanets.Count
        );

        // ── Step 2: Build spatial lookups ──

        // Planet → grid from Pages infobox
        var planetToGrid = new Dictionary<int, (int col, int row)>();
        var nameToGrid = new Dictionary<string, (int col, int row, string? region)>(
            StringComparer.OrdinalIgnoreCase
        );
        var gridFilter = BsonDocument.Parse(
            "{ 'infobox.Data': { $elemMatch: { Label: 'Grid square' } } }"
        );
        var gridPages = await _pages
            .Find(gridFilter)
            .Project(
                Builders<Page>
                    .Projection.Include(p => p.PageId)
                    .Include("title")
                    .Include("infobox.Data")
            )
            .ToListAsync(ct);

        foreach (var doc in gridPages)
        {
            var pageId = doc["_id"].AsInt32;
            var title = doc.Contains("title") ? doc["title"].AsString : null;
            var data =
                doc.Contains("infobox") && doc["infobox"].IsBsonDocument
                    ? doc["infobox"].AsBsonDocument
                    : null;
            if (data is null)
                continue;
            var dataArr =
                data.Contains("Data") && data["Data"].IsBsonArray ? data["Data"].AsBsonArray : null;
            if (dataArr is null)
                continue;

            string? gridStr = null;
            string? regionStr = null;

            foreach (var item in dataArr.OfType<BsonDocument>())
            {
                var label = item.GetValue("Label", "").AsString;
                var vals =
                    item.Contains("Values") && item["Values"].IsBsonArray
                        ? item["Values"].AsBsonArray
                        : null;
                var firstVal = vals?.FirstOrDefault()?.AsString;

                if (label == "Grid square" && gridStr is null)
                    gridStr = firstVal;
                if (label == "Region" && regionStr is null)
                    regionStr = firstVal;
            }

            if (gridStr is not null && TryParseGridSquare(gridStr, out var col, out var row))
            {
                planetToGrid.TryAdd(pageId, (col, row));
                var normRegion = regionStr is not null
                    ? NormaliseRegion(regionStr) ?? MapService.NormalizeRegionName(regionStr)
                    : null;
                if (title is not null)
                    nameToGrid.TryAdd(title, (col, row, normRegion));
            }
        }

        // Compute actual grid bounds from data
        var allCols = planetToGrid.Values.Select(g => g.col).ToList();
        var allRows = planetToGrid.Values.Select(g => g.row).ToList();
        var gridStartCol = allCols.Count > 0 ? allCols.Min() : 0;
        var gridStartRow = allRows.Count > 0 ? allRows.Min() : 0;
        var gridEndCol = allCols.Count > 0 ? allCols.Max() : 25;
        var gridEndRow = allRows.Count > 0 ? allRows.Max() : 20;
        var gridColumns = gridEndCol - gridStartCol + 1;
        var gridRows = gridEndRow - gridStartRow + 1;

        logger.LogInformation(
            "Galaxy map ETL: {GridById} planets with grid (by ID), {GridByName} (by name). Grid bounds: col {StartCol}-{EndCol} ({Cols} cols), row {StartRow}-{EndRow} ({Rows} rows)",
            planetToGrid.Count,
            nameToGrid.Count,
            gridStartCol,
            gridEndCol,
            gridColumns,
            gridStartRow,
            gridEndRow,
            gridRows
        );

        // ── Step 3: Build adjacency list from ALL edges for BFS ──
        // Include all continuities — Legends pages can link to Canon locations and vice versa

        var allEdges = await _edges
            .Find(FilterDefinition<RelationshipEdge>.Empty)
            .Project(
                Builders<RelationshipEdge>
                    .Projection.Include(e => e.FromId)
                    .Include(e => e.ToId)
                    .Include(e => e.ToName)
                    .Include(e => e.Label)
            )
            .ToListAsync(ct);

        var adjacency = new Dictionary<int, List<(int toId, string toName, string label)>>();
        foreach (var edge in allEdges)
        {
            var fromId = edge["fromId"].AsInt32;
            var toId = edge["toId"].AsInt32;
            var toName = edge["toName"].AsString;
            var label = edge["label"].AsString;
            if (!adjacency.TryGetValue(fromId, out var list))
                adjacency[fromId] = list = [];
            list.Add((toId, toName, label));
        }

        logger.LogInformation(
            "Galaxy map ETL: adjacency list from {Count} edges (all continuities)",
            allEdges.Count
        );

        // ── Step 4: Load ALL nodes from the knowledge graph with temporal data ──

        var eventKgNodes = await _nodes
            .Find(Builders<GraphNode>.Filter.Ne(n => n.StartYear, null))
            .ToListAsync(ct);

        // Debug: check continuity deserialization
        var contCounts = eventKgNodes
            .GroupBy(n => n.Continuity)
            .ToDictionary(g => g.Key, g => g.Count());
        var univCounts = eventKgNodes
            .GroupBy(n => n.Universe)
            .ToDictionary(g => g.Key, g => g.Count());
        logger.LogInformation(
            "Galaxy map ETL: continuity breakdown: {Counts}",
            string.Join(", ", contCounts.Select(kv => $"{kv.Key}={kv.Value}"))
        );
        logger.LogInformation(
            "Galaxy map ETL: universe breakdown: {Counts}",
            string.Join(", ", univCounts.Select(kv => $"{kv.Key}={kv.Value}"))
        );

        // Group by type (= lens) and by year
        var lensTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lensWithLocation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lensOutOfUniverse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var eventsByYear = new Dictionary<int, List<GraphNode>>();

        foreach (var node in eventKgNodes)
        {
            var lens = node.Type;
            lensTotals[lens] = lensTotals.GetValueOrDefault(lens) + 1;
            lensOutOfUniverse.TryAdd(lens, node.Universe == Universe.OutOfUniverse);

            // Use temporal facets for multi-year events (wars, campaigns span their duration)
            var startYear = node.StartYear!.Value;
            var endYear = node.EndYear ?? startYear;

            // For conflict/institutional types with both start and end, span all years
            var semanticPrefix = node.TemporalFacets.FirstOrDefault()?.Semantic.Split('.')[0];
            var spanYears =
                semanticPrefix is "conflict" or "institutional"
                && endYear > startYear
                && (endYear - startYear) <= 100; // cap to prevent huge ranges

            if (spanYears)
            {
                for (var y = startYear; y <= endYear; y++)
                {
                    if (!eventsByYear.TryGetValue(y, out var yl))
                        eventsByYear[y] = yl = [];
                    yl.Add(node);
                }
            }
            else
            {
                if (!eventsByYear.TryGetValue(startYear, out var yearList))
                    eventsByYear[startYear] = yearList = [];
                yearList.Add(node);
            }

            // Count events with resolvable locations
            if (planetToGrid.ContainsKey(node.PageId) || adjacency.ContainsKey(node.PageId))
                lensWithLocation[lens] = lensWithLocation.GetValueOrDefault(lens) + 1;
        }

        logger.LogInformation(
            "Galaxy map ETL: {Nodes} KG nodes with dates, {Types} types, {Years} distinct years",
            eventKgNodes.Count,
            lensTotals.Count,
            eventsByYear.Count
        );

        // ── Step 6: Load era nodes ──

        var eraNodes = await _nodes
            .Find(
                Builders<GraphNode>.Filter.And(
                    Builders<GraphNode>.Filter.Eq(n => n.Type, "Era"),
                    Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon),
                    Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)
                )
            )
            .ToListAsync(ct);

        var eras = eraNodes
            .Where(e => e.StartYear.HasValue)
            .Select(e => new TerritoryEra
            {
                Name = e.Name,
                StartYear = e.StartYear!.Value,
                EndYear = e.EndYear ?? e.StartYear!.Value,
            })
            .OrderBy(e => e.StartYear)
            .ToList();

        // ── Step 7: Collect all years (from governments + events) ──

        var allYears = new SortedSet<int>();
        foreach (var gov in govWithDates)
        {
            if (gov.StartYear.HasValue)
                allYears.Add(gov.StartYear.Value);
            if (gov.EndYear.HasValue)
                allYears.Add(gov.EndYear.Value);
        }
        foreach (var yr in eventsByYear.Keys)
            allYears.Add(yr);

        if (allYears.Count == 0)
        {
            logger.LogWarning("Galaxy map ETL: no years found");
            return;
        }
        logger.LogInformation("Galaxy map ETL: {Count} total years to process", allYears.Count);

        // ── Step 8: Build faction metadata ──

        var allFactionNames = factionRegionPlanets.Keys.ToList();
        var factionPages = await _pages
            .Find(
                Builders<Page>.Filter.And(
                    Builders<Page>.Filter.In(p => p.Title, allFactionNames),
                    Builders<Page>.Filter.Eq(p => p.Continuity, Continuity.Canon)
                )
            )
            .ToListAsync(ct);
        var factionPageMap = factionPages.ToDictionary(
            p => p.Title,
            p => p,
            StringComparer.OrdinalIgnoreCase
        );

        var factionInfoList = allFactionNames
            .Where(f => factionRegionPlanets[f].Values.Sum() >= 3)
            .Select(f =>
            {
                factionPageMap.TryGetValue(f, out var page);
                return new TerritoryFactionInfo
                {
                    Name = f,
                    Color = GetColor(f),
                    WikiUrl = page?.WikiUrl,
                    IconUrl = page?.Infobox?.ImageUrl,
                };
            })
            .OrderByDescending(f =>
                factionRegionPlanets.GetValueOrDefault(f.Name)?.Values.Sum() ?? 0
            )
            .ToList();

        // ── Step 9: Build year documents ──

        var yearDocs = new List<GalaxyYearDocument>();
        var yearDensityMap = new Dictionary<int, GalaxyYearDensity>();
        var totalResolved = 0;
        var totalUnresolved = 0;

        foreach (var year in allYears)
        {
            // ── Territory: active governments + region control ──
            var activeGovs = govWithDates
                .Where(g => g.StartYear <= year && (!g.EndYear.HasValue || g.EndYear >= year))
                .Select(g => g.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var regionControls = ComputeRegionControls(
                year,
                activeGovs,
                factionRegionPlanets,
                temporalAffiliations
            );

            // ── Events: resolve locations and build cells ──
            var cellMap = new Dictionary<(int col, int row), GalaxyYearEventCell>();
            var unresolvedEvents = new List<GalaxyYearEvent>();
            var lensCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perLensDensity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (eventsByYear.TryGetValue(year, out var yearEvents))
            {
                foreach (var node in yearEvents)
                {
                    var lens = node.Type;
                    lensCounts[lens] = lensCounts.GetValueOrDefault(lens) + 1;
                    perLensDensity[lens] = perLensDensity.GetValueOrDefault(lens) + 1;

                    // Resolve grid location via BFS from the node's pageId (direct, no title-matching)
                    var (place, region, col, row) = ResolveLocationByTraversal(
                        node.PageId,
                        adjacency,
                        planetToRegion,
                        planetToGrid,
                        maxDepth: 3
                    );

                    // Fallback: check property values for location names
                    if (col is null)
                    {
                        foreach (var label in LocationLabels)
                        {
                            if (!node.Properties.TryGetValue(label, out var vals))
                                continue;
                            foreach (var val in vals)
                            {
                                if (nameToGrid.TryGetValue(val, out var loc))
                                {
                                    col = loc.col;
                                    row = loc.row;
                                    place = val;
                                    region = loc.region;
                                    break;
                                }
                            }
                            if (col is not null)
                                break;
                        }
                    }

                    if (col is null || row is null)
                    {
                        totalUnresolved++;
                        if (unresolvedEvents.Count < 30)
                        {
                            var outcome = node
                                .Properties.GetValueOrDefault("Outcome")
                                ?.FirstOrDefault();
                            unresolvedEvents.Add(
                                new GalaxyYearEvent
                                {
                                    PageId = node.PageId,
                                    Title = node.Name,
                                    Lens = lens,
                                    Category = MapEventCategory(lens),
                                    Place = place,
                                    Outcome = outcome,
                                    WikiUrl = node.WikiUrl,
                                    ImageUrl = node.ImageUrl,
                                    Continuity = node.Continuity,
                                    Universe = node.Universe,
                                }
                            );
                        }
                        continue;
                    }
                    totalResolved++;

                    var key = (col.Value, row.Value);
                    if (!cellMap.TryGetValue(key, out var cell))
                    {
                        cell = new GalaxyYearEventCell
                        {
                            Col = col.Value,
                            Row = row.Value,
                            Region = region,
                        };
                        cellMap[key] = cell;
                    }

                    cell.Count++;
                    if (cell.Events.Count < 15)
                    {
                        var outcome = node
                            .Properties.GetValueOrDefault("Outcome")
                            ?.FirstOrDefault();

                        cell.Events.Add(
                            new GalaxyYearEvent
                            {
                                PageId = node.PageId,
                                Title = node.Name,
                                Lens = lens,
                                Category = MapEventCategory(lens),
                                Place = place,
                                Outcome = outcome,
                                WikiUrl = node.WikiUrl,
                                ImageUrl = node.ImageUrl,
                                Continuity = node.Continuity,
                                Universe = node.Universe,
                            }
                        );
                    }
                }
            }

            // Era for this year
            var era = eras.FirstOrDefault(e => year >= e.StartYear && year <= e.EndYear);

            yearDocs.Add(
                new GalaxyYearDocument
                {
                    Year = year,
                    YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
                    Era = era?.Name,
                    EraDescription = era?.Description,
                    Regions = regionControls,
                    EventCells = cellMap.Values.OrderByDescending(c => c.Count).ToList(),
                    UnresolvedEvents = unresolvedEvents,
                    LensCounts = lensCounts,
                }
            );

            yearDensityMap[year] = new GalaxyYearDensity
            {
                Year = year,
                Count = yearEvents?.Count ?? 0,
                CanonCount = yearEvents?.Count(n => n.Continuity == Continuity.Canon) ?? 0,
                LegendsCount = yearEvents?.Count(n => n.Continuity == Continuity.Legends) ?? 0,
                PerLens = perLensDensity,
            };
        }

        logger.LogInformation(
            "Galaxy map ETL: {Resolved} events resolved, {Unresolved} unresolved",
            totalResolved,
            totalUnresolved
        );

        // ── Step 10: Build lens summaries for overview ──

        var lensSummaries = lensTotals
            .Where(kv => kv.Value > 0)
            .Select(kv => new GalaxyLensSummary
            {
                Lens = kv.Key,
                TotalCount = kv.Value,
                WithLocationCount = lensWithLocation.GetValueOrDefault(kv.Key),
                OutOfUniverse = lensOutOfUniverse.GetValueOrDefault(kv.Key),
            })
            .OrderByDescending(s => s.TotalCount)
            .ToList();

        // ── Step 11: Build trade routes from KG edges ──

        var tradeRoutes = await BuildTradeRoutesAsync(adjacency, planetToRegion, planetToGrid, ct);
        logger.LogInformation(
            "Galaxy map ETL: {Count} trade routes built, {WithWaypoints} with 2+ grid waypoints",
            tradeRoutes.Count,
            tradeRoutes.Count(r => r.Waypoints.Count(w => w.Col.HasValue) >= 2)
        );

        // ── Step 12: Build overview document ──

        var overview = new GalaxyOverviewDocument
        {
            MinYear = allYears.Min,
            MaxYear = allYears.Max,
            GridColumns = gridColumns,
            GridRows = gridRows,
            GridStartCol = gridStartCol,
            GridStartRow = gridStartRow,
            AvailableYears = allYears.ToList(),
            Factions = factionInfoList,
            GalacticRegions = GalacticRegions.ToList(),
            Eras = eras,
            TradeRoutes = tradeRoutes,
            Lenses = lensSummaries,
            YearDensity = allYears
                .Select(y =>
                    yearDensityMap.GetValueOrDefault(y) ?? new GalaxyYearDensity { Year = y }
                )
                .ToList(),
        };

        // ── Step 13: Write to MongoDB ──

        var yearColl = _db.GetCollection<GalaxyYearDocument>(Collections.GalaxyYears);
        var overviewColl = _db.GetCollection<GalaxyOverviewDocument>(Collections.GalaxyYears);

        // Drop and recreate
        await _db.DropCollectionAsync(Collections.GalaxyYears, ct);

        if (yearDocs.Count > 0)
            await yearColl.InsertManyAsync(yearDocs, cancellationToken: ct);

        await overviewColl.ReplaceOneAsync(
            Builders<GalaxyOverviewDocument>.Filter.Eq(o => o.Id, "overview"),
            overview,
            new ReplaceOptions { IsUpsert = true },
            ct
        );

        await yearColl.Indexes.CreateOneAsync(
            new CreateIndexModel<GalaxyYearDocument>(
                Builders<GalaxyYearDocument>.IndexKeys.Ascending(d => d.Year)
            ),
            cancellationToken: ct
        );

        logger.LogInformation(
            "Galaxy map ETL: complete. {Years} year docs, {Lenses} lenses, {Factions} factions, overview stored",
            yearDocs.Count,
            lensSummaries.Count,
            factionInfoList.Count
        );
    }

    List<TerritoryRegionControl> ComputeRegionControls(
        int year,
        HashSet<string> activeGovs,
        Dictionary<string, Dictionary<string, int>> factionRegionPlanets,
        List<(string faction, string region, int? fromYear, int? toYear)> temporalAffiliations
    )
    {
        var regionControls = new List<TerritoryRegionControl>();

        // Build per-year faction-region counts from temporal affiliations
        var yearFactionRegion = new Dictionary<string, Dictionary<string, int>>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (faction, region, fromY, toY) in temporalAffiliations)
        {
            // Edge is active at this year if: no temporal bounds OR year is within [from, to]
            var active = fromY is null || (year >= fromY && (toY is null || year <= toY));
            if (!active || !activeGovs.Contains(faction))
                continue;
            if (!yearFactionRegion.TryGetValue(faction, out var rc))
                yearFactionRegion[faction] = rc = new();
            rc[region] = rc.GetValueOrDefault(region) + 1;
        }

        // Use temporal counts if we have them, otherwise fall back to static
        var effectiveCounts =
            yearFactionRegion.Count > 0 ? yearFactionRegion : factionRegionPlanets;

        foreach (var region in GalacticRegions)
        {
            var regionFactions = effectiveCounts
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
                                    Color = GetColor(def),
                                },
                            ],
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
                    Color = GetColor(dominant.Faction),
                },
            };

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
                            Color = GetColor(f),
                        }
                    );
                }
            }

            regionControls.Add(new() { Region = region, Factions = factions });
        }

        return regionControls;
    }

    /// <summary>
    /// Build trade routes from KG edges. Each route gets its endpoints, transit points,
    /// and waypoints resolved to grid coordinates via BFS through the adjacency list.
    /// </summary>
    async Task<List<GalaxyTradeRoute>> BuildTradeRoutesAsync(
        Dictionary<int, List<(int toId, string toName, string label)>> adjacency,
        Dictionary<int, string> planetToRegion,
        Dictionary<int, (int col, int row)> planetToGrid,
        CancellationToken ct
    )
    {
        // Load all trade route edges from KG
        var tradeRouteEdges = await _edges
            .Find(
                Builders<RelationshipEdge>.Filter.And(
                    Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, "TradeRoute"),
                    Builders<RelationshipEdge>.Filter.In(
                        e => e.Label,
                        new[]
                        {
                            "end_points",
                            "transit_points",
                            "has_object",
                            "junctions",
                            "regions",
                        }
                    )
                )
            )
            .ToListAsync(ct);

        // Group edges by route
        var routeEdges = tradeRouteEdges
            .GroupBy(e => (e.FromId, e.FromName))
            .ToDictionary(g => g.Key, g => g.ToList());

        var routes = new List<GalaxyTradeRoute>();

        foreach (var ((routeId, routeName), edges) in routeEdges)
        {
            var route = new GalaxyTradeRoute { Name = routeName, PageId = routeId };

            // Regions
            route.Regions = edges
                .Where(e => e.Label == "regions")
                .Select(e => NormaliseRegion(e.ToName))
                .Where(r => r is not null)
                .Cast<string>()
                .Distinct()
                .ToList();

            // Junctions (names of connecting routes)
            route.Junctions = edges
                .Where(e => e.Label == "junctions")
                .Select(e => e.ToName)
                .Distinct()
                .ToList();

            // Endpoints — resolve to grid coords
            route.Endpoints = edges
                .Where(e => e.Label == "end_points")
                .Select(e =>
                    ResolveWaypoint(e.ToId, e.ToName, adjacency, planetToRegion, planetToGrid)
                )
                .ToList();

            // Waypoints: transit_points first (they're ordered), then has_object as fallback
            var transitPoints = edges
                .Where(e => e.Label == "transit_points")
                .Select(e =>
                    ResolveWaypoint(e.ToId, e.ToName, adjacency, planetToRegion, planetToGrid)
                )
                .ToList();

            var otherObjects = edges
                .Where(e => e.Label == "has_object")
                .Select(e =>
                    ResolveWaypoint(e.ToId, e.ToName, adjacency, planetToRegion, planetToGrid)
                )
                .ToList();

            // Prefer transit_points (explicit waypoint ordering); use has_object if no transit points
            route.Waypoints = transitPoints.Count > 0 ? transitPoints : otherObjects;

            // Only include routes that have at least 2 grid-resolved points (endpoints + waypoints combined)
            var allPoints = route.Endpoints.Concat(route.Waypoints);
            if (allPoints.Count(w => w.Col.HasValue) >= 2)
                routes.Add(route);
        }

        return routes.OrderByDescending(r => r.Waypoints.Count + r.Endpoints.Count).ToList();
    }

    static GalaxyTradeRouteWaypoint ResolveWaypoint(
        int pageId,
        string name,
        Dictionary<int, List<(int toId, string toName, string label)>> adjacency,
        Dictionary<int, string> planetToRegion,
        Dictionary<int, (int col, int row)> planetToGrid
    )
    {
        var wp = new GalaxyTradeRouteWaypoint { Name = name, PageId = pageId };

        // Direct grid lookup
        if (planetToGrid.TryGetValue(pageId, out var grid))
        {
            wp.Col = grid.col;
            wp.Row = grid.row;
            if (planetToRegion.TryGetValue(pageId, out var region))
                wp.Region = region;
            return wp;
        }

        // BFS through adjacency to find grid coords (2 hops — enough for system→planet)
        var (place, region2, col, row) = ResolveLocationByTraversal(
            pageId,
            adjacency,
            planetToRegion,
            planetToGrid,
            maxDepth: 2
        );
        wp.Col = col;
        wp.Row = row;
        wp.Region = region2;

        return wp;
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

    static string? NormaliseRegion(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim();
        foreach (var region in GalacticRegions)
            if (lower == region.ToLowerInvariant())
                return region;
        if (lower.Contains("outer rim"))
            return "Outer Rim Territories";
        if (lower.Contains("mid rim"))
            return "Mid Rim";
        if (lower.Contains("inner rim"))
            return "Inner Rim";
        if (lower.Contains("core world"))
            return "Core Worlds";
        if (lower.Contains("deep core"))
            return "Deep Core";
        if (lower.Contains("colonies"))
            return "Colonies";
        if (lower.Contains("expansion"))
            return "Expansion Region";
        if (lower.Contains("hutt space"))
            return "Hutt Space";
        if (lower.Contains("unknown region"))
            return "Unknown Regions";
        if (lower.Contains("wild space"))
            return "Wild Space";
        return null;
    }

    static string GetColor(string faction)
    {
        if (FactionColors.TryGetValue(faction, out var color))
            return color;
        return ColorPalette[
            Math.Abs(faction.GetHashCode(StringComparison.OrdinalIgnoreCase)) % ColorPalette.Length
        ];
    }

    static string? MapEventCategory(string lens) =>
        lens switch
        {
            "Battle" or "Duel" => "Battle",
            "War" or "Campaign" => "War",
            "Treaty" => "Treaty",
            "Government" or "Organization" => "Government",
            "Mission" => "Campaign",
            _ => "Event",
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

                    if (planetToGrid.TryGetValue(toId, out var grid))
                    {
                        col = grid.col;
                        row = grid.row;
                        place = toName;
                        if (planetToRegion.TryGetValue(toId, out var r))
                            region = r;
                        return (place, region, col, row);
                    }

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
