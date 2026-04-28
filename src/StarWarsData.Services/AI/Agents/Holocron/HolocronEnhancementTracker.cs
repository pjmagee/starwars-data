using System.Collections.Concurrent;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron;

/// <summary>
/// Singleton tracker for live Holocron enhancement progress. Keyed by
/// <c>pageId</c> (one node = one active enhancement at a time per Design-020's
/// invariant). Mirror of <c>CharacterTimelineTracker</c> — same shape, same
/// polling pattern, different lifecycle enum (<see cref="HolocronJobStatus"/>).
///
/// The tracker is the FAST polling surface for the UI. The durable history of
/// runs lives in <c>kg.enrichment_jobs</c>; the tracker holds in-flight state
/// only and is cleared after a run completes (terminal entries are kept until
/// the next run starts so the UI can show "just completed: applied N
/// enrichments" briefly).
/// </summary>
public sealed class HolocronEnhancementTracker
{
    readonly ConcurrentDictionary<int, HolocronEnhancementStatus> _statuses = new();

    public HolocronEnhancementStatus? GetStatus(int pageId) => _statuses.GetValueOrDefault(pageId);

    /// <summary>
    /// True iff a status exists and is non-terminal. Mirrors
    /// <c>CharacterTimelineTracker.IsRunning</c> for the controller's
    /// "is there already a job for this page" check.
    /// </summary>
    public bool IsRunning(int pageId) => _statuses.TryGetValue(pageId, out var s) && s.Stage is not (HolocronJobStatus.Completed or HolocronJobStatus.Failed);

    /// <summary>
    /// Insert a fresh Queued status if no active run exists for this pageId.
    /// Returns false if a non-terminal entry is already present — the caller
    /// must reject the duplicate enqueue. Terminal entries (Completed/Failed)
    /// from a previous run are evicted first so a new run can claim the slot;
    /// otherwise the dictionary would hold the previous run's record forever
    /// and every subsequent kickoff would 409 even though IsRunning says false.
    /// </summary>
    public bool TryStart(int pageId, string? nodeName, string jobId)
    {
        if (_statuses.TryGetValue(pageId, out var existing) && existing.Stage is HolocronJobStatus.Completed or HolocronJobStatus.Failed)
        {
            _statuses.TryRemove(pageId, out _);
        }
        var status = new HolocronEnhancementStatus
        {
            JobId = jobId,
            Stage = HolocronJobStatus.Queued,
            Message = "Queued for enhancement...",
            StartedAt = DateTime.UtcNow,
            NodeName = nodeName,
        };
        return _statuses.TryAdd(pageId, status);
    }

    public void Update(int pageId, HolocronJobStatus stage, string message)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new HolocronEnhancementStatus
            {
                Stage = stage,
                Message = message,
                StartedAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Stage = stage;
                existing.Message = message;
                return existing;
            }
        );
    }

    public void UpdateProgress(
        int pageId,
        HolocronJobStatus stage,
        string message,
        int currentStep = 0,
        int totalSteps = 0,
        string? currentItem = null,
        int proposalsExtracted = 0,
        int enrichmentsApplied = 0
    )
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new HolocronEnhancementStatus
            {
                Stage = stage,
                Message = message,
                StartedAt = DateTime.UtcNow,
                CurrentStep = currentStep,
                TotalSteps = totalSteps,
                CurrentItem = currentItem,
                ProposalsExtracted = proposalsExtracted,
                EnrichmentsApplied = enrichmentsApplied,
            },
            (_, existing) =>
            {
                existing.Stage = stage;
                existing.Message = message;
                existing.CurrentStep = currentStep;
                existing.TotalSteps = totalSteps;
                existing.CurrentItem = currentItem;
                if (proposalsExtracted > 0)
                    existing.ProposalsExtracted = proposalsExtracted;
                if (enrichmentsApplied > 0)
                    existing.EnrichmentsApplied = enrichmentsApplied;
                return existing;
            }
        );
    }

    public void Complete(int pageId, string message) => Update(pageId, HolocronJobStatus.Completed, message);

    public void Fail(int pageId, string error)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new HolocronEnhancementStatus
            {
                Stage = HolocronJobStatus.Failed,
                Message = "Enhancement failed",
                Error = error,
                StartedAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Stage = HolocronJobStatus.Failed;
                existing.Message = "Enhancement failed";
                existing.Error = error;
                return existing;
            }
        );
    }

    public void AddActivityLog(int pageId, HolocronActivityLogEntry entry)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new HolocronEnhancementStatus { ActivityLog = [entry] },
            (_, existing) =>
            {
                existing.ActivityLog.Add(entry);
                return existing;
            }
        );
    }

    public void Clear(int pageId) => _statuses.TryRemove(pageId, out _);

    /// <summary>
    /// All currently in-flight runs. Used by the <c>/holocron/jobs/active</c> endpoint
    /// to drive the live-runs section of the jobs page.
    /// </summary>
    public List<(int PageId, HolocronEnhancementStatus Status)> GetActiveStatuses() =>
        _statuses.Where(kvp => kvp.Value.Stage is not (HolocronJobStatus.Completed or HolocronJobStatus.Failed)).Select(kvp => (kvp.Key, kvp.Value)).ToList();
}

/// <summary>
/// Live snapshot of one Holocron enhancement run. Returned by
/// <c>GET /api/holocron/jobs/{pageId}/status</c> for polling.
/// </summary>
public sealed class HolocronEnhancementStatus
{
    /// <summary>The <c>kg.enrichment_jobs._id</c> of this run, when one was created.</summary>
    public string? JobId { get; set; }

    public HolocronJobStatus Stage { get; set; } = HolocronJobStatus.Queued;
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string? Error { get; set; }
    public string? NodeName { get; set; }

    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? CurrentItem { get; set; }
    public int ProposalsExtracted { get; set; }
    public int EnrichmentsApplied { get; set; }

    /// <summary>
    /// Live activity feed populated from custom <c>WorkflowEvent</c>s emitted by
    /// the executors. Same shape as Timeline's <c>ActivityLogEntry</c> so the
    /// frontend can render with shared components.
    /// </summary>
    public List<HolocronActivityLogEntry> ActivityLog { get; set; } = [];
}

/// <summary>
/// One entry in the live activity feed for a Holocron run. Bridged from the
/// custom <c>WorkflowEvent</c>s by <c>HolocronEnhancementService</c>.
/// </summary>
public sealed class HolocronActivityLogEntry
{
    public DateTime Timestamp { get; set; }

    /// <summary>"Discovery" / "Bundling" / "Extraction" / "Consolidation" / "Apply"</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Discriminator for the frontend renderer.</summary>
    public string EntryType { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional structured payload for the activity-feed detail view.</summary>
    public object? Detail { get; set; }
}
