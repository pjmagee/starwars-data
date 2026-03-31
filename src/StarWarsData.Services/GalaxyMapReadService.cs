using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Read-only service for the unified galaxy map.
/// All data comes from pre-computed galaxy.years collection — zero aggregation at request time.
/// </summary>
public class GalaxyMapReadService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> settings)
{
    readonly IMongoCollection<GalaxyYearDocument> _yearDocs = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<GalaxyYearDocument>(Collections.GalaxyYears);
    readonly IMongoCollection<GalaxyOverviewDocument> _overviewColl = mongoClient
        .GetDatabase(settings.Value.DatabaseName).GetCollection<GalaxyOverviewDocument>(Collections.GalaxyYears);

    public async Task<GalaxyOverviewDocument?> GetOverviewAsync(CancellationToken ct = default)
    {
        return await _overviewColl
            .Find(Builders<GalaxyOverviewDocument>.Filter.Eq(o => o.Id, "overview"))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<GalaxyYearDocument?> GetYearAsync(int year, CancellationToken ct = default)
    {
        return await _yearDocs
            .Find(Builders<GalaxyYearDocument>.Filter.Eq(d => d.Year, year))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<GalaxyYearDocument>> GetYearRangeAsync(int fromYear, int toYear, CancellationToken ct = default)
    {
        return await _yearDocs
            .Find(Builders<GalaxyYearDocument>.Filter.And(
                Builders<GalaxyYearDocument>.Filter.Gte(d => d.Year, fromYear),
                Builders<GalaxyYearDocument>.Filter.Lte(d => d.Year, toYear)))
            .SortBy(d => d.Year)
            .ToListAsync(ct);
    }

    public async Task<Dictionary<string, TerritoryFactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        var overview = await GetOverviewAsync(ct);
        return overview?.Factions.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, TerritoryFactionInfo>(StringComparer.OrdinalIgnoreCase);
    }
}
