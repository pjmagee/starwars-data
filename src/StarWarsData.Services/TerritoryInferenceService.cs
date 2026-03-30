using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// ETL Phase 8: Infers galactic territory control from the AI-extracted knowledge graph.
/// Uses relationship edges (has_affiliation, controls, located_in_region, date_founded,
/// dissolved_in, reorganized_into) to build territory snapshots — no OpenAI batch calls needed.
/// </summary>
public partial class TerritoryInferenceService
{
    readonly IMongoCollection<RelationshipEdge> _edges;
    readonly IMongoDatabase _timelineDb;
    readonly IMongoCollection<TerritorySnapshot> _snapshots;
    readonly ILogger<TerritoryInferenceService> _logger;

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
        ["Sith Empire"] = "#8b0000",
        ["Mandalorian Government Council"] = "#2563eb",
    };

    static readonly string[] ColorPalette =
    [
        "#3b82f6", "#ef4444", "#6b7280", "#f97316", "#22c55e",
        "#991b1b", "#f59e0b", "#a8722a", "#1e3a5f", "#8b5cf6",
        "#ec4899", "#14b8a6", "#84cc16", "#d946ef", "#06b6d4",
    ];

    // Galactic regions (the spatial buckets for territory control)
    static readonly string[] GalacticRegions =
    [
        "Deep Core", "Core Worlds", "Colonies", "Inner Rim",
        "Expansion Region", "Mid Rim", "Outer Rim Territories",
        "Hutt Space", "Unknown Regions", "Wild Space",
    ];

    [GeneratedRegex(@"(\d[\d,]*)\s*(BBY|ABY)", RegexOptions.IgnoreCase)]
    private static partial Regex YearRegex();

    public TerritoryInferenceService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<TerritoryInferenceService> logger)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _edges = db.GetCollection<RelationshipEdge>(Collections.KgEdges);
        _timelineDb = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _snapshots = mongoClient.GetDatabase(settings.Value.DatabaseName)
            .GetCollection<TerritorySnapshot>(Collections.TerritorySnapshots);
        _logger = logger;
    }

    public async Task InferTerritoryAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Territory inference: starting (graph-based)...");

        // Step 1: Build planet → region lookup from the knowledge graph
        var planetToRegion = await BuildPlanetRegionLookupAsync(ct);
        _logger.LogInformation("Territory inference: {Count} planets mapped to regions", planetToRegion.Count);

        // Step 2: Build faction → planets mapping from affiliation + control edges
        var factionPlanets = await BuildFactionPlanetsAsync(ct);
        _logger.LogInformation("Territory inference: {Count} factions with planet affiliations",
            factionPlanets.Count);

        // Step 3: Build faction lifecycle (founded, dissolved, successor) from graph edges
        var factionLifecycle = await BuildFactionLifecycleAsync(ct);
        _logger.LogInformation("Territory inference: {Count} factions with lifecycle data",
            factionLifecycle.Count);

        // Step 4: Cross-reference — for each faction, count affiliated planets per region
        var factionRegionCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (faction, planets) in factionPlanets)
        {
            var regionCounts = new Dictionary<string, int>();
            foreach (var planet in planets)
            {
                if (planetToRegion.TryGetValue(planet, out var region))
                {
                    regionCounts[region] = regionCounts.GetValueOrDefault(region) + 1;
                }
            }
            if (regionCounts.Count > 0)
                factionRegionCounts[faction] = regionCounts;
        }

        _logger.LogInformation("Territory inference: {Count} factions have region-mapped planets",
            factionRegionCounts.Count);

        // Step 5: Determine time periods from faction lifecycles
        var periods = BuildTimePeriods(factionLifecycle, factionRegionCounts);
        _logger.LogInformation("Territory inference: {Count} time periods identified", periods.Count);

        // Step 6: Generate snapshots for each period
        var snapshots = new List<TerritorySnapshot>();

        foreach (var period in periods)
        {
            // Determine which factions are active in this period
            var activeFactions = factionRegionCounts.Keys
                .Where(f => IsFactionActive(f, period.Year, factionLifecycle))
                .ToList();

            // For each region, find the dominant active faction by affiliation count
            foreach (var region in GalacticRegions)
            {
                var regionFactions = activeFactions
                    .Where(f => factionRegionCounts[f].ContainsKey(region))
                    .Select(f => (Faction: f, Count: factionRegionCounts[f][region]))
                    .OrderByDescending(x => x.Count)
                    .ToList();

                if (regionFactions.Count == 0)
                {
                    // No data — assign to the default galactic government for this era
                    var defaultFaction = GetDefaultFaction(period.Year, factionLifecycle);
                    if (defaultFaction is not null)
                    {
                        snapshots.Add(new TerritorySnapshot
                        {
                            Year = period.Year,
                            Region = region,
                            Faction = defaultFaction,
                            Control = 1.0,
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
                    Year = period.Year,
                    Region = region,
                    Faction = dominant.Faction,
                    Control = Math.Max(0.3, control),
                    Contested = contested,
                    Color = GetColor(dominant.Faction),
                });

                // Add secondary factions for contested regions
                if (contested)
                {
                    foreach (var (faction, count) in regionFactions.Skip(1))
                    {
                        snapshots.Add(new TerritorySnapshot
                        {
                            Year = period.Year,
                            Region = region,
                            Faction = faction,
                            Control = Math.Max(0.1, (double)count / totalCount),
                            Contested = true,
                            Color = GetColor(faction),
                        });
                    }
                }
            }
        }

        // Step 7: Write to MongoDB
        await _snapshots.DeleteManyAsync(FilterDefinition<TerritorySnapshot>.Empty, ct);
        if (snapshots.Count > 0)
            await _snapshots.InsertManyAsync(snapshots, cancellationToken: ct);

        await _snapshots.Indexes.CreateManyAsync([
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys.Ascending(s => s.Year)),
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys
                    .Ascending(s => s.Year)
                    .Ascending(s => s.Region)),
        ], ct);

        _logger.LogInformation(
            "Territory inference: generated {Count} snapshots for {Periods} periods across {Factions} factions",
            snapshots.Count, periods.Count,
            snapshots.Select(s => s.Faction).Distinct().Count());
    }

    // ── Graph queries ──

    /// <summary>
    /// Build a lookup of planet/system name → galactic region from located_in_region edges.
    /// </summary>
    async Task<Dictionary<string, string>> BuildPlanetRegionLookupAsync(CancellationToken ct)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var filter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "located_in_region"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.In(e => e.FromType,
                ["CelestialBody", "System", "Sector", "SpaceStation"]));

        var edges = await _edges.Find(filter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName)
                .Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in edges)
        {
            var fromName = edge["fromName"].AsString;
            var toName = edge["toName"].AsString;
            // Normalise region names to match our canonical list
            var normalised = NormaliseRegion(toName);
            if (normalised is not null)
                lookup.TryAdd(fromName, normalised);
        }

        return lookup;
    }

    /// <summary>
    /// Build a mapping of faction → set of affiliated planets from has_affiliation + controls edges.
    /// </summary>
    async Task<Dictionary<string, HashSet<string>>> BuildFactionPlanetsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // has_affiliation: planet → government
        var affiliationFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "has_affiliation"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.In(e => e.FromType,
                ["CelestialBody", "System", "Sector"]),
            Builders<RelationshipEdge>.Filter.In(e => e.ToType,
                ["Government", "Organization"]));

        var affiliations = await _edges.Find(affiliationFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName)
                .Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in affiliations)
        {
            var planet = edge["fromName"].AsString;
            var faction = edge["toName"].AsString;
            if (!result.ContainsKey(faction))
                result[faction] = [];
            result[faction].Add(planet);
        }

        // controls: faction → planet (different direction)
        var controlsFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "controls"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.In(e => e.FromType,
                ["Government", "Organization"]),
            Builders<RelationshipEdge>.Filter.In(e => e.ToType,
                ["CelestialBody", "System", "Location"]));

        var controls = await _edges.Find(controlsFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName)
                .Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in controls)
        {
            var faction = edge["fromName"].AsString;
            var planet = edge["toName"].AsString;
            if (!result.ContainsKey(faction))
                result[faction] = [];
            result[faction].Add(planet);
        }

        // controlled_by: planet → faction
        var controlledByFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "controlled_by"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.In(e => e.FromType,
                ["CelestialBody", "System", "Location"]),
            Builders<RelationshipEdge>.Filter.In(e => e.ToType,
                ["Government", "Organization"]));

        var controlledBy = await _edges.Find(controlledByFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName)
                .Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in controlledBy)
        {
            var planet = edge["fromName"].AsString;
            var faction = edge["toName"].AsString;
            if (!result.ContainsKey(faction))
                result[faction] = [];
            result[faction].Add(planet);
        }

        _logger.LogInformation("Territory inference: graph edges — {Aff} affiliations, {Ctrl} controls, {CtrlBy} controlled_by",
            affiliations.Count, controls.Count, controlledBy.Count);

        return result;
    }

    /// <summary>
    /// Build faction lifecycle data from graph edges: date_founded, dissolved_in, reorganized_into.
    /// </summary>
    record FactionLifecycle(string Name, int? Founded, int? Dissolved, string? Successor);

    async Task<Dictionary<string, FactionLifecycle>> BuildFactionLifecycleAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, FactionLifecycle>(StringComparer.OrdinalIgnoreCase);

        // date_founded: Government → Year
        var foundedFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "date_founded"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, "Government"));

        var foundedEdges = await _edges.Find(foundedFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName).Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in foundedEdges)
        {
            var faction = edge["fromName"].AsString;
            var yearText = edge["toName"].AsString;
            var year = ParseYear(yearText);
            result.TryAdd(faction, new FactionLifecycle(faction, year, null, null));
        }

        // dissolved_in: Government → Year
        var dissolvedFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "dissolved_in"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, "Government"));

        var dissolvedEdges = await _edges.Find(dissolvedFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName).Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in dissolvedEdges)
        {
            var faction = edge["fromName"].AsString;
            var yearText = edge["toName"].AsString;
            var year = ParseYear(yearText);
            if (result.TryGetValue(faction, out var existing))
                result[faction] = existing with { Dissolved = year };
            else
                result[faction] = new FactionLifecycle(faction, null, year, null);
        }

        // reorganized_into: Government → Government (successor)
        var reorgFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "reorganized_into"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, "Government"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, "Government"));

        var reorgEdges = await _edges.Find(reorgFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName).Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in reorgEdges)
        {
            var faction = edge["fromName"].AsString;
            var successor = edge["toName"].AsString;
            if (result.TryGetValue(faction, out var existing))
                result[faction] = existing with { Successor = successor };
            else
                result[faction] = new FactionLifecycle(faction, null, null, successor);
        }

        // formed_into: Government → Government (same as reorganized_into)
        var formedFilter = Builders<RelationshipEdge>.Filter.And(
            Builders<RelationshipEdge>.Filter.Eq(e => e.Label, "formed_into"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<RelationshipEdge>.Filter.Eq(e => e.FromType, "Government"),
            Builders<RelationshipEdge>.Filter.Eq(e => e.ToType, "Government"));

        var formedEdges = await _edges.Find(formedFilter)
            .Project(Builders<RelationshipEdge>.Projection
                .Include(e => e.FromName).Include(e => e.ToName))
            .ToListAsync(ct);

        foreach (var edge in formedEdges)
        {
            var faction = edge["fromName"].AsString;
            var successor = edge["toName"].AsString;
            if (result.TryGetValue(faction, out var existing) && existing.Successor is null)
                result[faction] = existing with { Successor = successor };
            else if (!result.ContainsKey(faction))
                result[faction] = new FactionLifecycle(faction, null, null, successor);
        }

        foreach (var (name, lc) in result)
        {
            _logger.LogInformation("Territory inference: faction {Name} — founded: {Founded}, dissolved: {Dissolved}, successor: {Successor}",
                name, lc.Founded, lc.Dissolved, lc.Successor);
        }

        return result;
    }

    // ── Time period generation ──

    record TimePeriod(int Year, string Label);

    /// <summary>
    /// Build distinct time periods from faction lifecycle events.
    /// Each period represents a point where territory should be recalculated.
    /// </summary>
    List<TimePeriod> BuildTimePeriods(
        Dictionary<string, FactionLifecycle> lifecycle,
        Dictionary<string, Dictionary<string, int>> factionRegions)
    {
        var years = new SortedSet<int>();

        // Add lifecycle event years
        foreach (var (_, lc) in lifecycle)
        {
            if (lc.Founded.HasValue) years.Add(lc.Founded.Value);
            if (lc.Dissolved.HasValue) years.Add(lc.Dissolved.Value);
        }

        // Also add years from the Government timeline collection
        // to capture events not in graph edges
        var govYears = _timelineDb.GetCollection<TimelineEvent>(Collections.TimelinePrefix + "Government")
            .Find(Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon))
            .ToList()
            .Where(e => e.Year.HasValue)
            .Select(e => e.Demarcation == Demarcation.Bby ? -(int)e.Year! : (int)e.Year!)
            .Distinct();

        foreach (var y in govYears) years.Add(y);

        // Add battle years from the timeline collections
        foreach (var coll in new[] { "Battle", "War", "Campaign" })
        {
            var battleYears = _timelineDb.GetCollection<TimelineEvent>(Collections.TimelinePrefix + coll)
                .Find(Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon))
                .ToList()
                .Where(e => e.Year.HasValue)
                .Select(e => e.Demarcation == Demarcation.Bby ? -(int)e.Year! : (int)e.Year!)
                .Distinct();

            foreach (var y in battleYears) years.Add(y);
        }

        return years
            .Select(y => new TimePeriod(y, y <= 0 ? $"{Math.Abs(y)} BBY" : $"{y} ABY"))
            .ToList();
    }

    /// <summary>
    /// Check if a faction is active at a given year based on its lifecycle.
    /// If no lifecycle data, assume active (it has graph edges so it existed).
    /// </summary>
    static bool IsFactionActive(string faction, int year, Dictionary<string, FactionLifecycle> lifecycle)
    {
        if (!lifecycle.TryGetValue(faction, out var lc))
            return true; // No lifecycle data — assume active

        if (lc.Founded.HasValue && year < lc.Founded.Value)
            return false; // Not yet founded

        if (lc.Dissolved.HasValue && year > lc.Dissolved.Value)
            return false; // Already dissolved

        return true;
    }

    /// <summary>
    /// For years where no faction has affiliation data for a region,
    /// determine the default galactic government based on known succession.
    /// </summary>
    static string? GetDefaultFaction(int year, Dictionary<string, FactionLifecycle> lifecycle)
    {
        // Known galactic government succession for filling gaps
        if (year <= -19) return "Galactic Republic";
        if (year <= 4) return "Galactic Empire";
        if (year <= 34) return "New Republic";
        if (year <= 35) return "First Order";
        return "Galactic Republic"; // Fallback
    }

    // ── Helpers ──

    static string? NormaliseRegion(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim();
        foreach (var region in GalacticRegions)
        {
            if (lower == region.ToLowerInvariant())
                return region;
        }
        // Common variants
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
        if (lower.Contains("mid rim territories")) return "Mid Rim";
        if (lower.Contains("inner rim territories")) return "Inner Rim";
        return null;
    }

    static int? ParseYear(string text)
    {
        var match = YearRegex().Match(text);
        if (!match.Success) return null;
        var num = int.Parse(match.Groups[1].Value.Replace(",", ""));
        return match.Groups[2].Value.Equals("BBY", StringComparison.OrdinalIgnoreCase) ? -num : num;
    }

    static string GetColor(string faction)
    {
        if (FactionColors.TryGetValue(faction, out var color))
            return color;
        var hash = Math.Abs(faction.GetHashCode(StringComparison.OrdinalIgnoreCase));
        return ColorPalette[hash % ColorPalette.Length];
    }
}
