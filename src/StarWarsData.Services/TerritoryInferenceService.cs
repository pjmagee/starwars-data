using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// ETL Phase 8: Infers galactic territory control from battle outcomes, government lifecycles,
/// and location data. Generates TerritorySnapshot documents from real Wookieepedia data.
/// </summary>
public partial class TerritoryInferenceService
{
    readonly IMongoCollection<Page> _pages;
    readonly IMongoDatabase _timelineDb;
    readonly IMongoCollection<TerritorySnapshot> _snapshots;
    readonly ILogger<TerritoryInferenceService> _logger;

    // Known major factions and their display colors
    static readonly Dictionary<string, string> FactionColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Galactic Republic"] = "#3b82f6",
        ["Confederacy of Independent Systems"] = "#ef4444",
        ["Galactic Empire"] = "#6b7280",
        ["Alliance to Restore the Republic"] = "#f97316",
        ["New Republic"] = "#22c55e",
        ["First Order"] = "#991b1b",
        ["Resistance"] = "#f59e0b",
    };

    // Normalise outcome faction names to canonical names
    static readonly Dictionary<string, string> FactionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Republic"] = "Galactic Republic",
        ["Galactic Republic/Jedi Order"] = "Galactic Republic",
        ["Decisive Galactic Republic"] = "Galactic Republic",
        ["Republic/Jedi"] = "Galactic Republic",
        ["Clone trooper"] = "Galactic Republic",

        ["Separatist"] = "Confederacy of Independent Systems",
        ["Separatist Alliance"] = "Confederacy of Independent Systems",
        ["CIS"] = "Confederacy of Independent Systems",
        ["Confederacy"] = "Confederacy of Independent Systems",

        ["Imperial"] = "Galactic Empire",
        ["Galactic Empire tactical"] = "Galactic Empire",
        ["Galactic Empire/Sith"] = "Galactic Empire",
        ["Empire"] = "Galactic Empire",
        ["Decisive Galactic Empire"] = "Galactic Empire",
        ["Decisive Imperial"] = "Galactic Empire",

        ["Rebel Alliance"] = "Alliance to Restore the Republic",
        ["Rebel"] = "Alliance to Restore the Republic",
        ["Rebellion"] = "Alliance to Restore the Republic",
        ["Phoenix Cell"] = "Alliance to Restore the Republic",
        ["Spectres"] = "Alliance to Restore the Republic",

        ["New Republic/Resistance"] = "New Republic",
    };

    // Faction → Wookieepedia page title for looking up wiki URL and insignia image
    static readonly Dictionary<string, string> FactionPageTitles = new()
    {
        ["Galactic Republic"] = "Galactic Republic",
        ["Confederacy of Independent Systems"] = "Confederacy of Independent Systems",
        ["Galactic Empire"] = "Galactic Empire",
        ["Alliance to Restore the Republic"] = "Alliance to Restore the Republic",
        ["New Republic"] = "New Republic",
        ["First Order"] = "First Order",
        ["Resistance"] = "Resistance",
    };

    // Default control: which faction controls each region before any battles
    static readonly Dictionary<string, string> DefaultControl = new()
    {
        ["Deep Core"] = "Galactic Republic",
        ["Core Worlds"] = "Galactic Republic",
        ["Colonies"] = "Galactic Republic",
        ["Inner Rim"] = "Galactic Republic",
        ["Expansion Region"] = "Galactic Republic",
        ["Mid Rim"] = "Galactic Republic",
        ["Outer Rim Territories"] = "Galactic Republic",
        ["Hutt Space"] = "Hutt Space",
        ["Unknown Regions"] = "Chiss Ascendancy",
        ["Wild Space"] = "Galactic Republic",
    };

    [GeneratedRegex(@"^(.+?)\s+(?:victory|Victory)", RegexOptions.IgnoreCase)]
    private static partial Regex VictoryRegex();

    public TerritoryInferenceService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<TerritoryInferenceService> logger)
    {
        _pages = mongoClient.GetDatabase(settings.Value.PagesDb).GetCollection<Page>("Pages");
        _timelineDb = mongoClient.GetDatabase(settings.Value.TimelineEventsDb);
        _snapshots = mongoClient.GetDatabase(settings.Value.TerritoryControlDb)
            .GetCollection<TerritorySnapshot>("territory_snapshots");
        _logger = logger;
    }

    public async Task InferTerritoryAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Territory inference: starting...");

        // Step 1: Build location lookup (place name → region)
        var locationLookup = await BuildLocationLookupAsync(ct);
        _logger.LogInformation("Territory inference: {Count} locations mapped to grid/region", locationLookup.Count);

        // Step 2: Load all Canon battles with Place + Outcome
        var battleResults = await LoadBattleResultsAsync(locationLookup, ct);
        _logger.LogInformation("Territory inference: {Count} battle results with faction + region", battleResults.Count);

        // Step 3: Load government lifecycle events (established/dissolved dates)
        var govEvents = await LoadGovernmentLifecycleEventsAsync(ct);
        _logger.LogInformation("Territory inference: {Count} government lifecycle events", govEvents.Count);

        // Step 4: Determine year range
        var allYears = battleResults.Select(b => b.Year)
            .Concat(govEvents.Select(g => g.Year))
            .Distinct().OrderBy(y => y).ToList();

        if (allYears.Count == 0)
        {
            _logger.LogWarning("Territory inference: no data found");
            return;
        }

        var minYear = Math.Max(allYears.Min(), -100);
        var maxYear = Math.Min(allYears.Max(), 35);

        // Step 5: Build territory snapshots year by year
        var allRegions = DefaultControl.Keys.ToList();
        var currentControl = new Dictionary<string, string>(DefaultControl);
        var snapshots = new List<TerritorySnapshot>();

        for (var year = minYear; year <= maxYear; year++)
        {
            // Check government events for this year (establishment = take control of capital region)
            foreach (var gov in govEvents.Where(g => g.Year == year))
            {
                if (gov.EventType is "Date established" or "Date restored")
                {
                    // New faction takes over — apply to all regions if it's a galactic government
                    if (IsGalacticFaction(gov.Faction) && gov.CapitalRegion is not null)
                    {
                        // Take the capital region at minimum
                        currentControl[gov.CapitalRegion] = gov.Faction;
                    }
                }
                else if (gov.EventType is "Date dissolved" or "Date fragmented")
                {
                    // Faction loses control — successor takes over
                    if (gov.Successor is not null)
                    {
                        foreach (var region in allRegions)
                        {
                            if (currentControl.GetValueOrDefault(region) == gov.Faction)
                                currentControl[region] = gov.Successor;
                        }
                    }
                }
            }

            // Count battle victories per region for this year
            var yearBattles = battleResults.Where(b => b.Year == year).ToList();
            var regionVictories = new Dictionary<string, Dictionary<string, int>>();

            foreach (var battle in yearBattles)
            {
                if (battle.Region is null) continue;
                if (!regionVictories.ContainsKey(battle.Region))
                    regionVictories[battle.Region] = new();
                var factionCounts = regionVictories[battle.Region];
                factionCounts[battle.Faction] = factionCounts.GetValueOrDefault(battle.Faction) + 1;
            }

            // Apply battle results: if a faction wins significantly more battles in a region, they gain control
            foreach (var (region, factionCounts) in regionVictories)
            {
                var dominant = factionCounts.MaxBy(kv => kv.Value);
                if (dominant.Value >= 2 || (dominant.Value >= 1 && !currentControl.ContainsKey(region)))
                {
                    currentControl[region] = dominant.Key;
                }
            }

            // Handle the major galactic shifts explicitly from government events
            ApplyMajorShifts(year, currentControl);

            // Generate snapshots for this year
            foreach (var (region, faction) in currentControl)
            {
                var contested = regionVictories.ContainsKey(region) &&
                                regionVictories[region].Count > 1;
                var totalBattles = yearBattles.Count(b => b.Region == region);
                var control = contested ? 0.6 : 1.0;

                // Reduce control for regions far from capital during contested periods
                if (contested && regionVictories.TryGetValue(region, out var rc))
                {
                    var dominantCount = rc.GetValueOrDefault(faction);
                    var totalCount = rc.Values.Sum();
                    control = totalCount > 0 ? (double)dominantCount / totalCount : 0.5;
                    control = Math.Max(0.3, Math.Min(1.0, control));
                }

                var color = FactionColors.GetValueOrDefault(faction, "#888888");

                snapshots.Add(new TerritorySnapshot
                {
                    Year = year,
                    Region = region,
                    Faction = faction,
                    Control = control,
                    Contested = contested,
                    Color = color,
                });

                // Add secondary factions for contested regions
                if (contested && regionVictories.TryGetValue(region, out var vc))
                {
                    foreach (var (otherFaction, count) in vc.Where(kv => kv.Key != faction))
                    {
                        var otherControl = (double)count / vc.Values.Sum();
                        snapshots.Add(new TerritorySnapshot
                        {
                            Year = year,
                            Region = region,
                            Faction = otherFaction,
                            Control = Math.Max(0.1, otherControl),
                            Contested = true,
                            Color = FactionColors.GetValueOrDefault(otherFaction, "#888888"),
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
                Builders<TerritorySnapshot>.IndexKeys
                    .Ascending(s => s.Year)
                    .Ascending(s => s.Region)),
        ], ct);

        _logger.LogInformation("Territory inference: generated {Count} snapshots for years {Min} to {Max}",
            snapshots.Count, minYear, maxYear);
    }

    /// <summary>
    /// Apply known major galactic shifts that are implicit from government events
    /// but too significant to derive from battle counts alone.
    /// </summary>
    void ApplyMajorShifts(int year, Dictionary<string, string> control)
    {
        switch (year)
        {
            case -19:
                // Proclamation of the New Order — Empire replaces Republic everywhere
                foreach (var region in control.Keys.ToList())
                {
                    if (control[region] == "Galactic Republic")
                        control[region] = "Galactic Empire";
                    if (control[region] == "Confederacy of Independent Systems")
                        control[region] = "Galactic Empire";
                }
                break;

            case 4:
                // Battle of Endor — Empire begins fragmenting but still holds most territory
                // (battle results will naturally shift regions over next few years)
                break;

            case 5:
                // Battle of Jakku / Galactic Concordance — New Republic gains most territory
                foreach (var region in control.Keys.ToList())
                {
                    if (control[region] == "Galactic Empire")
                        control[region] = "New Republic";
                }
                break;

            case 34:
                // Hosnian Cataclysm — First Order seizes control
                foreach (var region in control.Keys.ToList())
                {
                    if (control[region] == "New Republic")
                        control[region] = "First Order";
                }
                break;

            case 35:
                // Battle of Exegol — First Order collapses (galaxy in flux)
                break;
        }
    }

    static bool IsGalacticFaction(string faction) =>
        faction is "Galactic Republic" or "Galactic Empire" or "New Republic" or "First Order";

    // ── Data loading ──

    record BattleResult(int Year, string Faction, string? Region);
    record GovernmentEvent(int Year, string Faction, string EventType, string? CapitalRegion, string? Successor);

    async Task<List<BattleResult>> LoadBattleResultsAsync(
        Dictionary<string, (int col, int row, string? region)> locationLookup,
        CancellationToken ct)
    {
        var results = new List<BattleResult>();
        var collection = _timelineDb.GetCollection<TimelineEvent>("Battle");

        var filter = Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon);
        var battles = await collection.Find(filter).ToListAsync(ct);

        foreach (var battle in battles)
        {
            var year = battle.Year;
            if (year is null) continue;
            var sortKey = battle.Demarcation == Demarcation.Bby ? -(int)year : (int)year;
            if (sortKey < -100 || sortKey > 35) continue;

            // Extract outcome
            var outcome = battle.Properties
                .FirstOrDefault(p => p.Label == "Outcome")?.Values.FirstOrDefault();
            if (outcome is null) continue;

            // Parse winning faction
            var faction = ParseWinningFaction(outcome);
            if (faction is null) continue;

            // Extract place and map to region
            var place = battle.Properties
                .FirstOrDefault(p => p.Label is "Place" or "Location")?.Values.FirstOrDefault();
            string? region = null;
            if (place is not null)
            {
                // Try matching the place (or parts of it) against the location lookup
                region = ResolveRegion(place, locationLookup);
            }

            results.Add(new BattleResult(sortKey, faction, region));
        }

        return results;
    }

    async Task<List<GovernmentEvent>> LoadGovernmentLifecycleEventsAsync(CancellationToken ct)
    {
        var events = new List<GovernmentEvent>();
        var collection = _timelineDb.GetCollection<TimelineEvent>("Government");

        var filter = Builders<TimelineEvent>.Filter.And(
            Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon),
            Builders<TimelineEvent>.Filter.In(e => e.DateEvent, [
                "Date established", "Date dissolved", "Date fragmented",
                "Date reorganized", "Date restored"
            ]));

        var govDocs = await collection.Find(filter).ToListAsync(ct);

        foreach (var doc in govDocs)
        {
            var title = doc.Title;
            if (title is null) continue;

            // Only track major factions
            var faction = NormaliseFaction(title);
            if (faction is null || !FactionColors.ContainsKey(faction)) continue;

            var year = doc.Year;
            if (year is null) continue;
            var sortKey = doc.Demarcation == Demarcation.Bby ? -(int)year : (int)year;
            if (sortKey < -100 || sortKey > 35) continue;

            var capitalValue = doc.Properties
                .FirstOrDefault(p => p.Label == "Capital")?.Values.FirstOrDefault();

            var formedFrom = doc.Properties
                .FirstOrDefault(p => p.Label == "Formed from")?.Values.FirstOrDefault();

            events.Add(new GovernmentEvent(
                sortKey, faction, doc.DateEvent ?? "",
                null, // capital region would need lookup
                formedFrom is not null ? NormaliseFaction(formedFrom) : null));
        }

        return events;
    }

    string? ParseWinningFaction(string outcome)
    {
        var match = VictoryRegex().Match(outcome);
        if (!match.Success) return null;

        var raw = match.Groups[1].Value.Trim();
        return NormaliseFaction(raw);
    }

    static string? NormaliseFaction(string raw)
    {
        if (FactionAliases.TryGetValue(raw, out var normalised))
            return normalised;
        if (FactionColors.ContainsKey(raw))
            return raw;
        // Try partial match
        foreach (var (alias, canonical) in FactionAliases)
        {
            if (raw.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return canonical;
        }
        foreach (var name in FactionColors.Keys)
        {
            if (raw.Contains(name, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    string? ResolveRegion(string place, Dictionary<string, (int col, int row, string? region)> lookup)
    {
        // Direct lookup
        if (lookup.TryGetValue(place, out var loc) && loc.region is not null)
            return loc.region;

        // Try each comma-separated part (e.g. "Echo Base, Hoth" → try "Hoth")
        var parts = place.Split(',', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (lookup.TryGetValue(part, out var partLoc) && partLoc.region is not null)
                return partLoc.region;
        }

        // Try removing suffixes like ", a planet"
        var cleaned = place.Split(',')[0].Trim();
        if (cleaned != place && lookup.TryGetValue(cleaned, out var cleanLoc) && cleanLoc.region is not null)
            return cleanLoc.region;

        return null;
    }

    // ── Reuse location lookup from GalaxyEventsService pattern ──

    async Task<Dictionary<string, (int col, int row, string? region)>> BuildLocationLookupAsync(CancellationToken ct)
    {
        var lookup = new Dictionary<string, (int col, int row, string? region)>(StringComparer.OrdinalIgnoreCase);

        // Systems with grid squares
        var sysFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression(":System$", "i")),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data", new BsonDocument("Label", "Grid square")));

        var sysRecs = await _pages.Find(sysFilter)
            .Project<Page>(Builders<Page>.Projection
                .Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync(ct);

        foreach (var rec in sysRecs)
        {
            var grid = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Grid square")?.Values.FirstOrDefault();
            if (TryParseGridSquare(grid, out var col, out var row))
            {
                var region = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Region")?.Values.FirstOrDefault();
                var normalised = region is not null ? MapService.NormalizeRegionName(region) : null;
                lookup.TryAdd(rec.Title, (col, row, normalised));
            }
        }

        // CelestialBodies with grid squares
        var cbFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Regex("infobox.Template", new BsonRegularExpression(":CelestialBody$", "i")),
            Builders<Page>.Filter.ElemMatch<BsonDocument>(
                "infobox.Data", new BsonDocument("Label", "Grid square")));

        var cbRecs = await _pages.Find(cbFilter)
            .Project<Page>(Builders<Page>.Projection
                .Include(p => p.Title).Include("infobox.Data"))
            .ToListAsync(ct);

        foreach (var rec in cbRecs)
        {
            var grid = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Grid square")?.Values.FirstOrDefault();
            if (TryParseGridSquare(grid, out var col, out var row))
            {
                var region = rec.Infobox?.Data.FirstOrDefault(d => d.Label == "Region")?.Values.FirstOrDefault();
                var normalised = region is not null ? MapService.NormalizeRegionName(region) : null;
                lookup.TryAdd(rec.Title, (col, row, normalised));
            }
        }

        return lookup;
    }

    static bool TryParseGridSquare(string? gridSquare, out int col, out int row)
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
