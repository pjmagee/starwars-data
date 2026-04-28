using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Read-side companion to <see cref="OpenAiSpendSyncService"/>. The sync writes to
/// <see cref="Collections.SpendDaily"/> nightly; this service serves the public
/// /costs page from the same collection. No live OpenAI calls — anyone with a
/// browser can hit it.
/// </summary>
public class OpenAiSpendQueryService
{
    readonly IMongoCollection<OpenAiSpendDay> _spend;

    public OpenAiSpendQueryService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _spend = db.GetCollection<OpenAiSpendDay>(Collections.SpendDaily);
    }

    public async Task<List<OpenAiSpendDay>> GetRecentDaysAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var cutoff = DateTime.UtcNow.Date.AddDays(-days).ToString("yyyy-MM-dd");
        return await _spend.Find(Builders<OpenAiSpendDay>.Filter.Gte(d => d.Date, cutoff)).SortBy(d => d.Date).ToListAsync(ct);
    }
}
