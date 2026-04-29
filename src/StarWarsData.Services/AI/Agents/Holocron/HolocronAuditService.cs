using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron;

/// <summary>
/// Per-proposal audit writer for the Holocron pipeline. Encapsulates inserts /
/// updates against <c>kg.holocron_audits</c> so each executor (consolidator,
/// verifier, apply) can record its verdict without duplicating Mongo plumbing.
///
/// <para>
/// Lifecycle: the consolidator calls <see cref="RecordAsync"/> for every unique
/// post-dedup proposal with a terminal outcome (<c>rejected_preflight_*</c> /
/// <c>rejected_evidence</c>) or <c>pending_verifier</c>. The verifier and apply
/// executors call <see cref="UpdateOutcomeAsync"/> to advance the row's
/// <see cref="HolocronAudit.Outcome"/> as the proposal moves through the pipeline.
/// </para>
/// </summary>
public sealed class HolocronAuditService
{
    readonly IMongoCollection<HolocronAudit> _audits;

    public HolocronAuditService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _audits = db.GetCollection<HolocronAudit>(Collections.KgHolocronAudits);
    }

    /// <summary>
    /// Insert one audit row. Returns the assigned <see cref="HolocronAudit.Id"/> so
    /// downstream stages can update by document id rather than re-deriving the
    /// natural key (faster + race-free across workflow restarts).
    /// </summary>
    public async Task<string> RecordAsync(HolocronAudit audit, CancellationToken ct)
    {
        audit.CreatedAt = DateTime.UtcNow;
        audit.UpdatedAt = DateTime.UtcNow;
        audit.Decisions.Add(new HolocronAuditDecision(Stage: "consolidation", Outcome: audit.Outcome, Reason: audit.OutcomeReason ?? string.Empty, At: audit.CreatedAt));
        await _audits.InsertOneAsync(audit, cancellationToken: ct);
        return audit.Id;
    }

    /// <summary>
    /// Promote an audit row's terminal outcome and append a new decision entry.
    /// Called by the verifier after each verdict and by the apply executor when an
    /// enrichment is durably written.
    /// </summary>
    public Task UpdateOutcomeAsync(string auditId, string stage, string outcome, string reason, CancellationToken ct)
    {
        var update = Builders<HolocronAudit>
            .Update.Set(a => a.Outcome, outcome)
            .Set(a => a.OutcomeReason, reason)
            .Set(a => a.UpdatedAt, DateTime.UtcNow)
            .Push(a => a.Decisions, new HolocronAuditDecision(stage, outcome, reason, DateTime.UtcNow));

        return _audits.UpdateOneAsync(Builders<HolocronAudit>.Filter.Eq(a => a.Id, auditId), update, cancellationToken: ct);
    }

    /// <summary>
    /// Bulk-update outcomes for a list of audit ids. Used by the verifier which
    /// processes all proposals in a single LLM call and then upserts in one batch.
    /// </summary>
    public Task BulkUpdateOutcomeAsync(IEnumerable<(string AuditId, string Outcome, string Reason)> updates, string stage, CancellationToken ct)
    {
        var writes = updates
            .Select(u =>
            {
                var update = Builders<HolocronAudit>
                    .Update.Set(a => a.Outcome, u.Outcome)
                    .Set(a => a.OutcomeReason, u.Reason)
                    .Set(a => a.UpdatedAt, DateTime.UtcNow)
                    .Push(a => a.Decisions, new HolocronAuditDecision(stage, u.Outcome, u.Reason, DateTime.UtcNow));
                return new UpdateOneModel<HolocronAudit>(Builders<HolocronAudit>.Filter.Eq(a => a.Id, u.AuditId), update);
            })
            .ToList<WriteModel<HolocronAudit>>();

        return writes.Count == 0 ? Task.CompletedTask : _audits.BulkWriteAsync(writes, cancellationToken: ct);
    }
}
