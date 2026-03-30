using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Manages job enable/disable toggles stored in MongoDB.
/// Each Hangfire recurring job checks its toggle at the start of execution.
/// </summary>
public class JobToggleService
{
    readonly IMongoCollection<JobToggle> _toggles;

    public JobToggleService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _toggles = db.GetCollection<JobToggle>(Collections.JobToggles);
    }

    /// <summary>
    /// Check if a job is enabled. Returns true if no toggle exists (default enabled).
    /// </summary>
    public async Task<bool> IsEnabledAsync(string jobId, CancellationToken ct = default)
    {
        var toggle = await _toggles.Find(t => t.JobId == jobId).FirstOrDefaultAsync(ct);
        return toggle?.Enabled ?? true;
    }

    /// <summary>Get all job toggles.</summary>
    public async Task<List<JobToggle>> GetAllAsync(CancellationToken ct = default) =>
        await _toggles.Find(FilterDefinition<JobToggle>.Empty).ToListAsync(ct);

    /// <summary>Set a job's enabled state.</summary>
    public async Task SetEnabledAsync(string jobId, bool enabled, CancellationToken ct = default)
    {
        await _toggles.UpdateOneAsync(
            t => t.JobId == jobId,
            Builders<JobToggle>.Update
                .Set(t => t.Enabled, enabled)
                .Set(t => t.UpdatedAt, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    /// <summary>Upsert a job toggle with full details.</summary>
    public async Task UpsertAsync(JobToggle toggle, CancellationToken ct = default)
    {
        toggle.UpdatedAt = DateTime.UtcNow;
        await _toggles.ReplaceOneAsync(
            t => t.JobId == toggle.JobId,
            toggle,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }
}
