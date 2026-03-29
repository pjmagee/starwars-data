using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class TerritoryControlService
{
    readonly IMongoCollection<TerritorySnapshot> _snapshots;
    readonly IMongoCollection<Page> _pages;
    readonly IMongoDatabase _timelineDb;
    readonly ILogger<TerritoryControlService> _logger;

    // Collections with events relevant to territory control
    static readonly string[] EventCollections = ["Battle", "War", "Campaign", "Treaty", "Event", "Duel", "Mission", "Government"];

    public TerritoryControlService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<TerritoryControlService> logger
    )
    {
        var db = mongoClient.GetDatabase(settings.Value.TerritoryControlDb);
        _snapshots = db.GetCollection<TerritorySnapshot>("territory_snapshots");
        _pages = mongoClient.GetDatabase(settings.Value.PagesDb).GetCollection<Page>("Pages");
        _timelineDb = mongoClient.GetDatabase(settings.Value.TimelineEventsDb);
        _logger = logger;
    }

    // ── ETL: Load seed data into MongoDB ──

    public async Task LoadSeedDataAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Territory control: loading seed data...");

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("territory-control-canon.json"))
            ?? throw new InvalidOperationException("Seed data resource not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var seedData = await JsonSerializer.DeserializeAsync<SeedData>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }, ct) ?? throw new InvalidOperationException("Failed to deserialize seed data");

        // Drop existing data and rebuild
        await _snapshots.DeleteManyAsync(FilterDefinition<TerritorySnapshot>.Empty, ct);

        var snapshots = new List<TerritorySnapshot>();

        foreach (var entry in seedData.Entries)
        {
            var factionInfo = seedData.Factions.GetValueOrDefault(entry.Faction);
            var color = factionInfo?.Color ?? "#888888";

            foreach (var (region, control) in entry.Regions)
            {
                // Expand the year range into individual yearly snapshots
                for (var year = entry.YearStart; year <= entry.YearEnd; year++)
                {
                    snapshots.Add(new TerritorySnapshot
                    {
                        Year = year,
                        Region = region,
                        Faction = entry.Faction,
                        Control = control,
                        Contested = entry.Contested,
                        Color = color,
                        Note = entry.Note,
                    });
                }
            }
        }

        if (snapshots.Count > 0)
        {
            await _snapshots.InsertManyAsync(snapshots, cancellationToken: ct);
        }

        // Create indexes
        await _snapshots.Indexes.CreateManyAsync([
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys.Ascending(s => s.Year)),
            new CreateIndexModel<TerritorySnapshot>(
                Builders<TerritorySnapshot>.IndexKeys
                    .Ascending(s => s.Year)
                    .Ascending(s => s.Region)),
        ], ct);

        _logger.LogInformation(
            "Territory control: loaded {Count} snapshots from {Entries} entries",
            snapshots.Count, seedData.Entries.Count);
    }

    // ── Query methods ──

    public async Task<TerritoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var factions = await _snapshots.DistinctAsync(s => s.Faction, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);
        var regions = await _snapshots.DistinctAsync(s => s.Region, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);
        var years = await _snapshots.DistinctAsync(s => s.Year, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);

        var factionList = await factions.ToListAsync(ct);
        var regionList = await regions.ToListAsync(ct);
        var yearList = await years.ToListAsync(ct);

        // Load eras from the timeline-events Era collection (Canon only)
        var eras = await GetCanonErasAsync(ct);

        return new TerritoryOverview
        {
            MinYear = yearList.Count > 0 ? yearList.Min() : 0,
            MaxYear = yearList.Count > 0 ? yearList.Max() : 0,
            Factions = factionList.OrderBy(f => f).ToList(),
            Regions = regionList.OrderBy(r => r).ToList(),
            Eras = eras,
        };
    }

    public async Task<TerritoryYearResponse> GetYearAsync(int year, CancellationToken ct = default)
    {
        var filter = Builders<TerritorySnapshot>.Filter.Eq(s => s.Year, year);
        var snapshots = await _snapshots.Find(filter).ToListAsync(ct);

        var regionGroups = snapshots
            .GroupBy(s => s.Region)
            .Select(g => new TerritoryRegionControl
            {
                Region = g.Key,
                Factions = g.Select(s => new TerritoryFactionControl
                {
                    Faction = s.Faction,
                    Control = s.Control,
                    Contested = s.Contested,
                    Color = s.Color,
                    Note = s.Note,
                }).OrderByDescending(f => f.Control).ToList(),
            })
            .OrderBy(r => r.Region)
            .ToList();

        // Find the current era from the timeline-events Era collection
        var eras = await GetCanonErasAsync(ct);
        var era = eras.FirstOrDefault(e => year >= e.StartYear && year <= e.EndYear);

        // Query real events from the timeline database
        var keyEvents = await GetTimelineEventsForYearAsync(year, ct);

        return new TerritoryYearResponse
        {
            Year = year,
            YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
            Era = era?.Name,
            EraDescription = era?.Description,
            EraConflicts = era?.Conflicts ?? [],
            EraImportantEvents = era?.ImportantEvents ?? [],
            Regions = regionGroups,
            KeyEvents = keyEvents,
        };
    }

    public async Task<Dictionary<string, FactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        var seedData = await LoadSeedFileAsync(ct);
        if (seedData is null) return new();

        var result = new Dictionary<string, FactionInfo>();

        // Look up each faction's wiki URL and image from MongoDB Pages collection
        var pageTitles = seedData.Factions.Values
            .Where(f => f.PageTitle is not null)
            .Select(f => f.PageTitle!)
            .ToList();

        var filter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.In(p => p.Title, pageTitles),
            Builders<Page>.Filter.Eq(p => p.Continuity, Continuity.Canon)
        );

        var matchedPages = await _pages.Find(filter).ToListAsync(ct);

        var pageLookup = new Dictionary<string, (string? wikiUrl, string? imageUrl)>();
        foreach (var page in matchedPages)
        {
            pageLookup[page.Title] = (page.WikiUrl, page.Infobox?.ImageUrl);
        }

        foreach (var (factionName, seedInfo) in seedData.Factions)
        {
            var pageTitle = seedInfo.PageTitle ?? factionName;
            pageLookup.TryGetValue(pageTitle, out var pageInfo);

            result[factionName] = new FactionInfo
            {
                Color = seedInfo.Color,
                WikiUrl = pageInfo.wikiUrl,
                IconUrl = pageInfo.imageUrl,
            };
        }

        return result;
    }

    /// <summary>
    /// Load canonical eras from the timeline-events Era collection.
    /// Each era appears twice (start/end year), so we deduplicate by title.
    /// </summary>
    async Task<List<TerritoryEra>> GetCanonErasAsync(CancellationToken ct)
    {
        var eraCollection = _timelineDb.GetCollection<TimelineEvent>("Era");
        var filter = Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon);
        var eraDocs = await eraCollection.Find(filter).ToListAsync(ct);

        // Deduplicate by title — each era may have two docs (start + end year)
        var grouped = eraDocs.GroupBy(e => e.Title ?? "");

        var eras = new List<TerritoryEra>();
        foreach (var group in grouped)
        {
            if (string.IsNullOrEmpty(group.Key)) continue;

            var docs = group.ToList();
            var sortKeys = docs.Select(d =>
            {
                var y = (int)(d.Year ?? 0);
                return d.Demarcation == Demarcation.Bby ? -y : y;
            }).OrderBy(k => k).ToList();

            var yearsValue = docs.First().Properties
                .FirstOrDefault(p => p.Label == "Years")?.Values.FirstOrDefault();
            var conflicts = docs.First().Properties
                .FirstOrDefault(p => p.Label == "Conflicts")?.Values ?? [];
            var importantEvents = docs.First().Properties
                .FirstOrDefault(p => p.Label == "Important events")?.Values ?? [];

            eras.Add(new TerritoryEra
            {
                Name = group.Key,
                StartYear = sortKeys.First(),
                EndYear = sortKeys.Last(),
                Description = yearsValue,
                Conflicts = conflicts,
                ImportantEvents = importantEvents,
            });
        }

        // Filter to eras in our territory range and sort
        return eras
            .Where(e => e.EndYear >= -100 && e.StartYear <= 35)
            .OrderBy(e => e.StartYear)
            .ToList();
    }

    /// <summary>
    /// Query real timeline events from the starwars-timeline-events database for a specific year.
    /// Returns Battle, War, Campaign, Treaty, Event, Duel, Mission, and Government events.
    /// </summary>
    async Task<List<TerritoryKeyEvent>> GetTimelineEventsForYearAsync(int year, CancellationToken ct)
    {
        var absYear = Math.Abs(year);
        var demarcation = year <= 0 ? Demarcation.Bby : Demarcation.Aby;

        var existingCollections = (await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct)).ToList();
        var events = new List<TerritoryKeyEvent>();

        foreach (var collectionName in EventCollections)
        {
            if (!existingCollections.Contains(collectionName))
                continue;

            var collection = _timelineDb.GetCollection<TimelineEvent>(collectionName);

            var filter = Builders<TimelineEvent>.Filter.And(
                Builders<TimelineEvent>.Filter.Eq(e => e.Year, absYear),
                Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, demarcation),
                Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon)
            );

            var docs = await collection.Find(filter)
                .Limit(20)
                .ToListAsync(ct);

            foreach (var doc in docs)
            {
                var place = doc.Properties
                    .FirstOrDefault(p => p.Label is "Place" or "Location" or "System" or "Headquarters")
                    ?.Values.FirstOrDefault();

                var outcome = doc.Properties
                    .FirstOrDefault(p => p.Label is "Outcome" or "Result")
                    ?.Values.FirstOrDefault();

                var title = doc.Title ?? "Unknown";
                var wikiUrl = $"https://starwars.fandom.com/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";

                var descParts = new List<string>();
                if (!string.IsNullOrEmpty(place)) descParts.Add(place);
                if (!string.IsNullOrEmpty(outcome)) descParts.Add(outcome);

                events.Add(new TerritoryKeyEvent
                {
                    Year = year,
                    Title = title,
                    Description = descParts.Count > 0 ? string.Join(". ", descParts) : null,
                    WikiUrl = wikiUrl,
                    Category = collectionName,
                });
            }
        }

        // Sort: Battles and Wars first, then by title
        return events
            .OrderByDescending(e => e.Category is "Battle" or "War" or "Campaign")
            .ThenBy(e => e.Title)
            .Take(30)
            .ToList();
    }

    private async Task<SeedData?> LoadSeedFileAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("territory-control-canon.json"));
        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return await JsonSerializer.DeserializeAsync<SeedData>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }, ct);
    }

    // ── Seed data model ──

    record SeedData
    {
        public Dictionary<string, SeedFactionInfo> Factions { get; init; } = new();
        public List<SeedEntry> Entries { get; init; } = [];
    }

    /// <summary>Seed data: only color + page title for MongoDB lookup.</summary>
    record SeedFactionInfo
    {
        public string Color { get; init; } = "#888888";
        public string? PageTitle { get; init; }
    }

    /// <summary>API response: enriched with wikiUrl and iconUrl from MongoDB.</summary>
    public record FactionInfo
    {
        public string Color { get; init; } = "#888888";
        public string? WikiUrl { get; init; }
        public string? IconUrl { get; init; }
    }

    record SeedEntry
    {
        public string Faction { get; init; } = string.Empty;
        public int YearStart { get; init; }
        public int YearEnd { get; init; }
        public Dictionary<string, double> Regions { get; init; } = new();
        public bool Contested { get; init; }
        public string? Note { get; init; }
    }
}
