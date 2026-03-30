using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Read-only query service for pre-computed territory control data.
/// All data is pre-built by TerritoryInferenceService — this service
/// just reads documents from MongoDB. No dynamic queries, no timeline
/// collection scanning, no location lookups.
/// </summary>
public class TerritoryControlService
{
    readonly IMongoCollection<TerritoryYearDocument> _yearDocs;
    readonly IMongoCollection<TerritoryOverviewDocument> _overviewColl;
    readonly IMongoCollection<TerritorySnapshot> _snapshots;

    public TerritoryControlService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<TerritoryControlService> logger)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _yearDocs = db.GetCollection<TerritoryYearDocument>(Collections.TerritoryYears);
        _overviewColl = db.GetCollection<TerritoryOverviewDocument>(Collections.TerritoryYears);
        _snapshots = db.GetCollection<TerritorySnapshot>(Collections.TerritorySnapshots);
    }

    /// <summary>Read the pre-computed overview document.</summary>
    public async Task<TerritoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var doc = await _overviewColl
            .Find(Builders<TerritoryOverviewDocument>.Filter.Eq(o => o.Id, "overview"))
            .FirstOrDefaultAsync(ct);

        if (doc is null)
            return new TerritoryOverview();

        return new TerritoryOverview
        {
            MinYear = doc.MinYear,
            MaxYear = doc.MaxYear,
            AvailableYears = doc.AvailableYears,
            Factions = doc.Factions.Select(f => f.Name).ToList(),
            Regions = doc.Regions,
            Eras = doc.Eras,
        };
    }

    /// <summary>Read a pre-computed year document. Single find, no computation.</summary>
    public async Task<TerritoryYearResponse> GetYearAsync(int year, CancellationToken ct = default)
    {
        var doc = await _yearDocs
            .Find(Builders<TerritoryYearDocument>.Filter.Eq(d => d.Year, year))
            .FirstOrDefaultAsync(ct);

        if (doc is null)
            return new TerritoryYearResponse
            {
                Year = year,
                YearDisplay = year <= 0 ? $"{Math.Abs(year)} BBY" : $"{year} ABY",
            };

        return new TerritoryYearResponse
        {
            Year = doc.Year,
            YearDisplay = doc.YearDisplay,
            Era = doc.Era,
            EraDescription = doc.EraDescription,
            Regions = doc.Regions,
            KeyEvents = doc.KeyEvents,
        };
    }

    /// <summary>Read pre-computed faction metadata from the overview document.</summary>
    public async Task<Dictionary<string, FactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        var doc = await _overviewColl
            .Find(Builders<TerritoryOverviewDocument>.Filter.Eq(o => o.Id, "overview"))
            .FirstOrDefaultAsync(ct);

        if (doc is null) return new();

        return doc.Factions.ToDictionary(
            f => f.Name,
            f => new FactionInfo
            {
                Color = f.Color,
                WikiUrl = f.WikiUrl,
                IconUrl = f.IconUrl,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public record FactionInfo
    {
        public string Color { get; init; } = "#888888";
        public string? WikiUrl { get; init; }
        public string? IconUrl { get; init; }
    }
}
