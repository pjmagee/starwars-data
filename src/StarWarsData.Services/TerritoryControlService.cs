using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class TerritoryControlService
{
    readonly IMongoCollection<TerritorySnapshot> _snapshots;
    readonly ILogger<TerritoryControlService> _logger;

    public TerritoryControlService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<TerritoryControlService> logger
    )
    {
        var db = mongoClient.GetDatabase(settings.Value.TerritoryControlDb);
        _snapshots = db.GetCollection<TerritorySnapshot>("territory_snapshots");
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

        // Load eras from seed data
        var seedFile = await LoadSeedFileAsync(ct);
        var eras = seedFile?.Eras.Select(e => new TerritoryEra
        {
            Name = e.Name,
            StartYear = e.StartYear,
            EndYear = e.EndYear,
        }).ToList() ?? [];

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

        return new TerritoryYearResponse
        {
            Year = year,
            YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
            Regions = regionGroups,
        };
    }

    public async Task<Dictionary<string, FactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        var seedData = await LoadSeedFileAsync(ct);
        return seedData?.Factions ?? new();
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
        public Dictionary<string, FactionInfo> Factions { get; init; } = new();
        public List<SeedEra> Eras { get; init; } = [];
        public List<SeedEntry> Entries { get; init; } = [];
    }

    record SeedEra
    {
        public string Name { get; init; } = string.Empty;
        public int StartYear { get; init; }
        public int EndYear { get; init; }
    }

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
