using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// ETL Phase 8: Infers galactic territory control from the temporal knowledge graph.
/// Uses kg.nodes (Government lifecycles) + kg.edges (affiliated_with, in_region)
/// to determine which faction controls which galactic region at each point in time.
/// No OpenAI calls — pure graph aggregation.
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
    readonly IMongoDatabase _db = mongoClient.GetDatabase(settings.Value.DatabaseName);

    // Display colors for well-known factions
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

    public async Task InferTerritoryAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Territory inference: starting (knowledge graph)...");

        // Step 1: Load Canon government nodes with temporal data
        var governments = await _nodes
            .Find(Builders<GraphNode>.Filter.And(
                Builders<GraphNode>.Filter.Eq(n => n.Type, "Government"),
                Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon)))
            .ToListAsync(ct);

        var govWithDates = governments.Where(g => g.StartYear.HasValue).ToList();
        logger.LogInformation("Territory inference: {Total} Canon governments, {WithDates} with temporal data",
            governments.Count, govWithDates.Count);

        // Step 2: Build planet → region lookup from in_region edges
        var planetToRegion = new Dictionary<int, string>();
        var regionEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "in_region"),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .Project(Builders<RelationshipEdge>.Projection.Include(e => e.FromId).Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in regionEdges)
        {
            var fromId = edge["fromId"].AsInt32;
            var region = NormaliseRegion(edge["toName"].AsString);
            if (region is not null)
                planetToRegion.TryAdd(fromId, region);
        }
        logger.LogInformation("Territory inference: {Count} planets mapped to regions", planetToRegion.Count);

        // Step 3: Build faction → planets per region from affiliated_with edges
        var factionRegionPlanets = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var affiliationEdges = await _edges
            .Find(Builders<RelationshipEdge>.Filter.And(
                Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "affiliated_with"),
                Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon)))
            .ToListAsync(ct);

        foreach (var edge in affiliationEdges)
        {
            var planetId = edge.FromId;
            var faction = edge.ToName;
            if (!planetToRegion.TryGetValue(planetId, out var region)) continue;

            if (!factionRegionPlanets.TryGetValue(faction, out var regionCounts))
            {
                regionCounts = new Dictionary<string, int>();
                factionRegionPlanets[faction] = regionCounts;
            }
            regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
        }

        logger.LogInformation("Territory inference: {Count} factions with region-mapped affiliations",
            factionRegionPlanets.Count);

        // Step 4: Determine time periods from government lifecycles + battle dates
        var eventYears = new SortedSet<int>();
        foreach (var gov in govWithDates)
        {
            if (gov.StartYear.HasValue) eventYears.Add(gov.StartYear.Value);
            if (gov.EndYear.HasValue) eventYears.Add(gov.EndYear.Value);
        }

        // Also add battle years for more granularity
        var battleNodes = await _nodes
            .Find(Builders<GraphNode>.Filter.And(
                Builders<GraphNode>.Filter.Eq(n => n.Type, "Battle"),
                Builders<GraphNode>.Filter.Eq(n => n.Continuity, Continuity.Canon),
                Builders<GraphNode>.Filter.Ne(n => n.StartYear, null)))
            .Project(Builders<GraphNode>.Projection.Include(n => n.StartYear))
            .ToListAsync(ct);

        foreach (var b in battleNodes)
        {
            var yr = b.Contains("startYear") && !b["startYear"].IsBsonNull ? b["startYear"].AsInt32 : (int?)null;
            if (yr.HasValue) eventYears.Add(yr.Value);
        }

        logger.LogInformation("Territory inference: {Count} distinct event years", eventYears.Count);

        if (eventYears.Count == 0)
        {
            logger.LogWarning("Territory inference: no event years found");
            return;
        }

        // Step 5: Build snapshots for each event year
        var snapshots = new List<TerritorySnapshot>();

        // Build a lookup of government node PageIds for cross-referencing
        var govByName = govWithDates.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

        foreach (var year in eventYears)
        {
            // Find active governments at this year
            var activeGovs = govWithDates
                .Where(g => g.StartYear <= year && (!g.EndYear.HasValue || g.EndYear >= year))
                .Select(g => g.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // For each region, find the dominant active faction by affiliation count
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
                    // No affiliation data — use default government for this era
                    var defaultFaction = GetDefaultFaction(year);
                    if (defaultFaction is not null && activeGovs.Contains(defaultFaction))
                    {
                        snapshots.Add(new TerritorySnapshot
                        {
                            Year = year, Region = region,
                            Faction = defaultFaction, Control = 1.0,
                            Color = GetColor(defaultFaction),
                        });
                    }
                    continue;
                }

                var dominant = regionFactions[0];
                var totalCount = regionFactions.Sum(x => x.Count);
                var control = (double)dominant.Count / totalCount;
                var contested = regionFactions.Count > 1 && control < 0.7;

                snapshots.Add(new TerritorySnapshot
                {
                    Year = year, Region = region,
                    Faction = dominant.Faction,
                    Control = Math.Max(0.3, control),
                    Contested = contested,
                    Color = GetColor(dominant.Faction),
                });

                // Secondary factions for contested regions
                if (contested)
                {
                    foreach (var (faction, count) in regionFactions.Skip(1))
                    {
                        snapshots.Add(new TerritorySnapshot
                        {
                            Year = year, Region = region,
                            Faction = faction,
                            Control = Math.Max(0.1, (double)count / totalCount),
                            Contested = true,
                            Color = GetColor(faction),
                        });
                    }
                }
            }
        }

        // Step 6: Write to MongoDB
        await _snapshots.DeleteManyAsync(FilterDefinition<TerritorySnapshot>.Empty, ct);
        if (snapshots.Count > 0)
            await _snapshots.InsertManyAsync(snapshots, cancellationToken: ct);

        await _snapshots.Indexes.CreateManyAsync([
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys.Ascending(s => s.Year)),
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys.Ascending(s => s.Year).Ascending(s => s.Region)),
        ], ct);

        logger.LogInformation(
            "Territory inference: generated {Count} snapshots for {Years} event years across {Factions} factions",
            snapshots.Count, eventYears.Count,
            snapshots.Select(s => s.Faction).Distinct().Count());
    }

    /// <summary>Default galactic government for eras without affiliation data.</summary>
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
        if (lower.Contains("colonies") || lower == "the interior") return "Colonies";
        if (lower.Contains("expansion")) return "Expansion Region";
        if (lower.Contains("hutt space")) return "Hutt Space";
        if (lower.Contains("unknown region")) return "Unknown Regions";
        if (lower.Contains("wild space")) return "Wild Space";
        return null;
    }

    static string GetColor(string faction)
    {
        if (FactionColors.TryGetValue(faction, out var color)) return color;
        var hash = Math.Abs(faction.GetHashCode(StringComparison.OrdinalIgnoreCase));
        return ColorPalette[hash % ColorPalette.Length];
    }
}
