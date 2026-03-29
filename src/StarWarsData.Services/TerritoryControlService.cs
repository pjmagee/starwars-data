using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

/// <summary>
/// Query service for territory control data. Read-only — data is generated
/// by TerritoryInferenceService (ETL Phase 7b).
/// </summary>
public class TerritoryControlService
{
    readonly IMongoCollection<TerritorySnapshot> _snapshots;
    readonly IMongoCollection<Page> _pages;
    readonly IMongoDatabase _timelineDb;
    readonly ILogger<TerritoryControlService> _logger;

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

    // ── Query methods ──

    public async Task<TerritoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var factions = await _snapshots.DistinctAsync(s => s.Faction, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);
        var regions = await _snapshots.DistinctAsync(s => s.Region, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);
        var years = await _snapshots.DistinctAsync(s => s.Year, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);

        var factionList = await factions.ToListAsync(ct);
        var regionList = await regions.ToListAsync(ct);
        var yearList = await years.ToListAsync(ct);

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

        var eras = await GetCanonErasAsync(ct);
        var era = eras.FirstOrDefault(e => year >= e.StartYear && year <= e.EndYear);

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

    /// <summary>
    /// Get faction info (color, wiki URL, insignia) by looking up Pages in MongoDB.
    /// Colors come from TerritoryInferenceService.FactionColors; wiki URLs and images
    /// are resolved from the Pages collection.
    /// </summary>
    public async Task<Dictionary<string, FactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        // Get distinct factions from the snapshots
        var factions = await _snapshots.DistinctAsync(s => s.Faction, FilterDefinition<TerritorySnapshot>.Empty, cancellationToken: ct);
        var factionList = await factions.ToListAsync(ct);

        // Look up pages for each faction
        var matchFilter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.In(p => p.Title, factionList),
            Builders<Page>.Filter.Eq(p => p.Continuity, Continuity.Canon)
        );
        var matchedPages = await _pages.Find(matchFilter).ToListAsync(ct);
        var pageLookup = matchedPages.ToDictionary(p => p.Title, p => p);

        // Also get colors from the snapshots (each snapshot has a color)
        var colorLookup = new Dictionary<string, string>();
        var colorFilter = Builders<TerritorySnapshot>.Filter.Empty;
        var colorDocs = await _snapshots.Find(colorFilter)
            .Project(Builders<TerritorySnapshot>.Projection
                .Include(s => s.Faction)
                .Include(s => s.Color))
            .Limit(1000)
            .ToListAsync(ct);

        foreach (var doc in colorDocs)
        {
            var faction = doc["Faction"]?.AsString ?? doc["faction"]?.AsString;
            var color = doc["Color"]?.AsString ?? doc["color"]?.AsString;
            if (faction is not null && color is not null)
                colorLookup.TryAdd(faction, color);
        }

        var result = new Dictionary<string, FactionInfo>();
        foreach (var faction in factionList)
        {
            pageLookup.TryGetValue(faction, out var page);
            colorLookup.TryGetValue(faction, out var color);

            result[faction] = new FactionInfo
            {
                Color = color ?? "#888888",
                WikiUrl = page?.WikiUrl,
                IconUrl = page?.Infobox?.ImageUrl,
            };
        }

        return result;
    }

    // ── Eras from timeline DB ──

    async Task<List<TerritoryEra>> GetCanonErasAsync(CancellationToken ct)
    {
        var eraCollection = _timelineDb.GetCollection<TimelineEvent>("Era");
        var filter = Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon);
        var eraDocs = await eraCollection.Find(filter).ToListAsync(ct);

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

        return eras
            .Where(e => e.EndYear >= -100 && e.StartYear <= 35)
            .OrderBy(e => e.StartYear)
            .ToList();
    }

    // ── Timeline events for a year ──

    async Task<List<TerritoryKeyEvent>> GetTimelineEventsForYearAsync(int year, CancellationToken ct)
    {
        var absYear = Math.Abs(year);
        var demarcation = year <= 0 ? Demarcation.Bby : Demarcation.Aby;

        var existingCollections = (await _timelineDb.ListCollectionNamesAsync(cancellationToken: ct)).ToList();
        var events = new List<TerritoryKeyEvent>();

        foreach (var collectionName in EventCollections)
        {
            if (!existingCollections.Contains(collectionName)) continue;

            var collection = _timelineDb.GetCollection<TimelineEvent>(collectionName);
            var filter = Builders<TimelineEvent>.Filter.And(
                Builders<TimelineEvent>.Filter.Eq(e => e.Year, absYear),
                Builders<TimelineEvent>.Filter.Eq(e => e.Demarcation, demarcation),
                Builders<TimelineEvent>.Filter.Eq(e => e.Continuity, Continuity.Canon)
            );

            var docs = await collection.Find(filter).Limit(20).ToListAsync(ct);

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

        return events
            .OrderByDescending(e => e.Category is "Battle" or "War" or "Campaign")
            .ThenBy(e => e.Title)
            .Take(30)
            .ToList();
    }

    /// <summary>API response: enriched faction info with wiki URL and insignia from MongoDB.</summary>
    public record FactionInfo
    {
        public string Color { get; init; } = "#888888";
        public string? WikiUrl { get; init; }
        public string? IconUrl { get; init; }
    }
}
