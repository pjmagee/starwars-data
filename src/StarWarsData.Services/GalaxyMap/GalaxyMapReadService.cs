using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Read-only service for the unified galaxy map.
/// All data comes from pre-computed galaxy.years collection — zero aggregation at request time.
/// </summary>
public class GalaxyMapReadService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
{
    readonly IMongoCollection<GalaxyYearDocument> _yearDocs = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GalaxyYearDocument>(Collections.GalaxyYears);
    readonly IMongoCollection<GalaxyOverviewDocument> _overviewColl = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<GalaxyOverviewDocument>(Collections.GalaxyYears);

    public async Task<GalaxyOverviewDocument?> GetOverviewAsync(Continuity? continuity = null, CancellationToken ct = default)
    {
        // Without a continuity filter, a simple Find is enough.
        if (continuity is null || continuity == Continuity.Both)
        {
            return await _overviewColl.Find(Builders<GalaxyOverviewDocument>.Filter.Eq(o => o.Id, "overview")).FirstOrDefaultAsync(ct);
        }

        // DB-level filter of the embedded Eras array via $filter. Keep eras whose
        // stored Continuity matches the selected value, or is Both/Unknown (so
        // "shared" eras appear under either continuity).
        var keep = new BsonArray { continuity.Value.ToString(), nameof(Continuity.Both), nameof(Continuity.Unknown) };

        // IMPORTANT: these string literals must match the actual BSON element names
        // on disk, not the C# property names. `GalaxyOverviewDocument.Eras` is
        // annotated [BsonElement("eras")] (lowercase), while TerritoryEra.Continuity
        // has no explicit element attribute so it serialises as "Continuity"
        // (PascalCase default). Using nameof(...) here would produce the wrong
        // field name and create a duplicate key that breaks strict deserialisation.
        const string erasField = "eras";
        const string eraContinuityField = nameof(TerritoryEra.Continuity);

        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("_id", "overview")),
            new BsonDocument(
                "$addFields",
                new BsonDocument(
                    erasField,
                    new BsonDocument(
                        "$filter",
                        new BsonDocument
                        {
                            { "input", "$" + erasField },
                            { "as", "e" },
                            { "cond", new BsonDocument("$in", new BsonArray { "$$e." + eraContinuityField, keep }) },
                        }
                    )
                )
            ),
        };

        return await _overviewColl.Aggregate<GalaxyOverviewDocument>(pipeline, cancellationToken: ct).FirstOrDefaultAsync(ct);
    }

    public async Task<GalaxyYearDocument?> GetYearAsync(int year, CancellationToken ct = default)
    {
        return await _yearDocs.Find(Builders<GalaxyYearDocument>.Filter.Eq(d => d.Year, year)).FirstOrDefaultAsync(ct);
    }

    public async Task<List<GalaxyYearDocument>> GetYearRangeAsync(int fromYear, int toYear, CancellationToken ct = default)
    {
        return await _yearDocs
            .Find(Builders<GalaxyYearDocument>.Filter.And(Builders<GalaxyYearDocument>.Filter.Gte(d => d.Year, fromYear), Builders<GalaxyYearDocument>.Filter.Lte(d => d.Year, toYear)))
            .SortBy(d => d.Year)
            .ToListAsync(ct);
    }

    public async Task<Dictionary<string, TerritoryFactionInfo>> GetFactionInfoAsync(CancellationToken ct = default)
    {
        var overview = await GetOverviewAsync(ct: ct);
        return overview?.Factions.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, TerritoryFactionInfo>(StringComparer.OrdinalIgnoreCase);
    }
}
