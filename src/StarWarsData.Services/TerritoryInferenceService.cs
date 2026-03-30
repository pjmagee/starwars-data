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
public class TerritoryInferenceService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> settings,
    ILogger<TerritoryInferenceService> logger)
{
    readonly IMongoCollection<GraphNode> _nodes = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
    readonly IMongoCollection<RelationshipEdge> _edges = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);
    readonly IMongoCollection<TerritorySnapshot> _snapshots = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<TerritorySnapshot>(Collections.TerritorySnapshots);
    readonly IMongoCollection<TerritoryYearDocument> _yearDocs = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<TerritoryYearDocument>(Collections.TerritoryYears);
    readonly IMongoCollection<Page> _pages = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<Page>(Collections.Pages);
    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);

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
        "#3b82f6", "#ef4444", "#6b7280", "#f97316", "#22c55e",
        "#991b1b", "#f59e0b", "#a8722a", "#1e3a5f", "#8b5cf6",
        "#ec4899", "#14b8a6", "#84cc16", "#d946ef", "#06b6d4",
    ];

    static readonly string[] GalacticRegions =
    [
        "Deep Core", "Core Worlds", "Colonies", "Inner Rim",
        "Expansion Region", "Mid Rim", "Outer Rim Territories",
        "Hutt Space", "Unknown Regions", "Wild Space",
    ];

    static readonly string[] EventTypes = ["Battle", "War", "Campaign", "Government", "Treaty", "Event"];

    public async Task InferTerritoryAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Territory inference: starting...");

        // ── Step 1: Load data from knowledge graph ──

        var governments = await _nodes
            .Find(Builders<GraphNode>.Filter.And(
                Builders<GraphNode>.Filter.Eq(n => n.Type, "Government"),
                Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        var govWithDates = governments.Where(g => g.StartYear.HasValue).ToList();

        // Planet → region from in_region edges
        var planetToRegion = new Dictionary<int, string>();
        var regionEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "in_region"),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync(ct);
        foreach (var edge in regionEdges)
        {
            var region = NormaliseRegion(edge["toName"].AsString);
            if (region is not null) planetToRegion.TryAdd(edge["fromId"].AsInt32, region);
        }

        // Faction → planets per region from affiliated_with edges
        var factionRegionPlanets = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var affiliationEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "affiliated_with"),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        foreach (var edge in affiliationEdges)
        {
            if (!planetToRegion.TryGetValue(edge.FromId, out var region)) continue;
            if (!factionRegionPlanets.TryGetValue(edge.ToName, out var regionCounts))
                factionRegionPlanets[edge.ToName] = regionCounts = new();
            regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
        }

        logger.LogInformation("Territory inference: {Govs} govs, {Planets} planet-regions, {Factions} factions",
            govWithDates.Count, planetToRegion.Count, factionRegionPlanets.Count);

        // ── Step 2: Load ALL event nodes (for key events per year) ──

        var eventNodes = await _nodes
            .Find(Builders<GraphNode>.Filter.And(
                Builders<GraphNode>.Filter.In(n => n.Type, EventTypes),
                Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon),
                Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)))
            .ToListAsync(ct);

        // Group events by year for fast lookup
        var eventsByYear = eventNodes
            .GroupBy(n => n.StartYear!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        logger.LogInformation("Territory inference: {Count} Canon event nodes with dates", eventNodes.Count);

        // ── Step 3: Load eras from Era nodes ──

        var eraNodes = await _nodes
            .Find(Builders<GraphNode>.Filter.And(
                Builders<GraphNode>.Filter.Eq(n => n.Type, "Era"),
                Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon),
                Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)))
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

        // ── Step 4: Collect event years ──

        var eventYears = new SortedSet<int>();
        foreach (var gov in govWithDates)
        {
            if (gov.StartYear.HasValue) eventYears.Add(gov.StartYear.Value);
            if (gov.EndYear.HasValue) eventYears.Add(gov.EndYear.Value);
        }
        foreach (var yr in eventsByYear.Keys) eventYears.Add(yr);

        if (eventYears.Count == 0) { logger.LogWarning("No event years"); return; }
        logger.LogInformation("Territory inference: {Count} event years", eventYears.Count);

        // ── Step 5: Build faction metadata (with wiki URLs and icons from Pages) ──

        var allFactionNames = factionRegionPlanets.Keys.ToList();
        var factionPages = await _pages
            .Find(Builders<Page>.Filter.And(
                Builders<Page>.Filter.In(p => p.Title, allFactionNames),
                Builders<Page>.Filter.Eq(p => p.Continuity, Continuity.Canon)))
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
                    Color = GetColor(f),
                    WikiUrl = page?.WikiUrl,
                    IconUrl = page?.Infobox?.ImageUrl,
                };
            })
            .OrderByDescending(f => factionRegionPlanets.GetValueOrDefault(f.Name)?.Values.Sum() ?? 0)
            .ToList();

        // ── Step 5b: Build planet → grid coordinate lookup from Pages infobox ──

        var planetToGrid = new Dictionary<int, (int col, int row)>();
        var gridFilter = MongoDB.Bson.BsonDocument.Parse(
            "{ 'infobox.Data': { $elemMatch: { Label: 'Grid square' } } }");
        var gridPages = await _pages.Find(gridFilter)
            .Project(Builders<Page>.Projection.Include(p => p.PageId).Include("infobox.Data"))
            .ToListAsync(ct);

        foreach (var doc in gridPages)
        {
            var pageId = doc["_id"].AsInt32;
            var data = doc.Contains("infobox") && doc["infobox"].IsBsonDocument
                ? doc["infobox"].AsBsonDocument : null;
            if (data is null) continue;
            var dataArr = data.Contains("Data") && data["Data"].IsBsonArray ? data["Data"].AsBsonArray : null;
            if (dataArr is null) continue;

            foreach (var item in dataArr.OfType<MongoDB.Bson.BsonDocument>())
            {
                if (item.GetValue("Label", "").AsString != "Grid square") continue;
                var vals = item.Contains("Values") && item["Values"].IsBsonArray
                    ? item["Values"].AsBsonArray : null;
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

        // Location edge lookup — for resolving ANY entity to a grid position
        // Try multiple relationship types: took_place_at, has_capital, headquartered_at, located_at, homeworld
        var locationLabels = new[] { "took_place_at", "has_capital", "headquartered_at", "located_at", "homeworld", "on_celestial_body" };
        var locationEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.In(e => e.Label, locationLabels),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        var entityLocationLookup = locationEdges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.ToList());

        logger.LogInformation("Territory inference: {Count} location edges across {Labels} label types",
            locationEdges.Count, locationLabels.Length);

        // Took_place_at edge lookup — group ALL edges per event so we can try multiple targets
        var tookPlaceEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "took_place_at"),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .ToListAsync(ct);
        var eventPlaceLookup = tookPlaceEdges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var year in eventYears)
        {
            var activeGovs = govWithDates
                .Where(g => g.StartYear <= year && (!g.EndYear.HasValue || g.EndYear >= year))
                .Select(g => g.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Region control
            var regionControls = new List<TerritoryRegionControl>();
            foreach (var region in GalacticRegions)
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
                        regionControls.Add(new TerritoryRegionControl
                        {
                            Region = region,
                            Factions = [new() { Faction = def, Control = 1.0, Color = GetColor(def) }]
                        });
                        snapshots.Add(new() { Year = year, Region = region, Faction = def, Control = 1.0, Color = GetColor(def) });
                    }
                    continue;
                }

                var total = regionFactions.Sum(x => x.Count);
                var dominant = regionFactions[0];
                var control = (double)dominant.Count / total;
                var contested = regionFactions.Count > 1 && control < 0.7;

                var factions = new List<TerritoryFactionControl>
                {
                    new() { Faction = dominant.Faction, Control = Math.Max(0.3, control), Contested = contested, Color = GetColor(dominant.Faction) }
                };
                snapshots.Add(new() { Year = year, Region = region, Faction = dominant.Faction, Control = Math.Max(0.3, control), Contested = contested, Color = GetColor(dominant.Faction) });

                if (contested)
                {
                    foreach (var (f, c) in regionFactions.Skip(1))
                    {
                        var oc = Math.Max(0.1, (double)c / total);
                        factions.Add(new() { Faction = f, Control = oc, Contested = true, Color = GetColor(f) });
                        snapshots.Add(new() { Year = year, Region = region, Faction = f, Control = oc, Contested = true, Color = GetColor(f) });
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
                    string? place = null, region = null;
                    int? col = null, row = null;

                    if (eventPlaceLookup.TryGetValue(evt.PageId, out var placeEdges))
                    {
                        // Try each took_place_at target until we find one with grid coordinates
                        foreach (var pe in placeEdges)
                        {
                            place ??= pe.ToName;
                            if (pe.ToId > 0)
                            {
                                if (region is null && planetToRegion.TryGetValue(pe.ToId, out var pr))
                                    region = pr;
                                if (col is null && planetToGrid.TryGetValue(pe.ToId, out var grid))
                                {
                                    col = grid.col;
                                    row = grid.row;
                                    place = pe.ToName; // prefer the name of the one with coordinates
                                }
                            }
                        }
                    }

                    // Fallback: try all location edges (capital, headquarters, located_at, etc.)
                    if (col is null && entityLocationLookup.TryGetValue(evt.PageId, out var locEdges))
                    {
                        foreach (var le in locEdges)
                        {
                            if (le.ToId > 0)
                            {
                                if (region is null && planetToRegion.TryGetValue(le.ToId, out var lr))
                                    region = lr;
                                if (planetToGrid.TryGetValue(le.ToId, out var lg))
                                {
                                    col = lg.col;
                                    row = lg.row;
                                    place ??= le.ToName;
                                    break;
                                }
                            }
                        }
                    }

                    var outcome = evt.Properties.GetValueOrDefault("Outcome")?.FirstOrDefault();

                    keyEvents.Add(new TerritoryKeyEvent
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
                    });
                }
            }

            // Era for this year
            var era = eras.FirstOrDefault(e => year >= e.StartYear && year <= e.EndYear);

            yearDocs.Add(new TerritoryYearDocument
            {
                Year = year,
                YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
                Era = era?.Name,
                EraDescription = era?.Description,
                Regions = regionControls,
                KeyEvents = keyEvents,
            });
        }

        // ── Step 7: Build overview document ──

        var overview = new TerritoryOverviewDocument
        {
            MinYear = eventYears.Min,
            MaxYear = eventYears.Max,
            AvailableYears = eventYears.ToList(),
            Factions = factionInfoList,
            Eras = eras,
            Regions = GalacticRegions.ToList(),
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
        await overviewColl.ReplaceOneAsync(
            Builders<TerritoryOverviewDocument>.Filter.Eq(o => o.Id, "overview"),
            overview, new ReplaceOptions { IsUpsert = true }, ct);

        await _yearDocs.Indexes.CreateOneAsync(
            new CreateIndexModel<TerritoryYearDocument>(
                Builders<TerritoryYearDocument>.IndexKeys.Ascending(d => d.Year)),
            cancellationToken: ct);

        logger.LogInformation(
            "Territory inference: {Snapshots} snapshots, {Years} year docs, {Factions} factions, overview stored",
            snapshots.Count, yearDocs.Count, factionInfoList.Count);
    }

    static string? GetDefaultFaction(int year) => year switch
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
            if (lower == region.ToLowerInvariant()) return region;
        if (lower.Contains("outer rim")) return "Outer Rim Territories";
        if (lower.Contains("mid rim")) return "Mid Rim";
        if (lower.Contains("inner rim")) return "Inner Rim";
        if (lower.Contains("core world")) return "Core Worlds";
        if (lower.Contains("deep core")) return "Deep Core";
        if (lower.Contains("colonies")) return "Colonies";
        if (lower.Contains("expansion")) return "Expansion Region";
        if (lower.Contains("hutt space")) return "Hutt Space";
        if (lower.Contains("unknown region")) return "Unknown Regions";
        if (lower.Contains("wild space")) return "Wild Space";
        return null;
    }

    static string GetColor(string faction)
    {
        if (FactionColors.TryGetValue(faction, out var color)) return color;
        return ColorPalette[Math.Abs(faction.GetHashCode(StringComparison.OrdinalIgnoreCase)) % ColorPalette.Length];
    }

    static bool TryParseGridSquare(string gridSquare, out int col, out int row)
    {
        col = 0; row = 0;
        if (string.IsNullOrWhiteSpace(gridSquare)) return false;
        var parts = gridSquare.Split('-', 2);
        if (parts.Length != 2) return false;
        var letter = parts[0].Trim().ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') return false;
        if (!int.TryParse(parts[1].Trim(), out var num) || num < 1 || num > 20) return false;
        col = letter[0] - 'A';
        row = num - 1;
        return true;
    }
}
