using System.Collections.Concurrent;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Singleton tracker for character timeline generation progress.
/// Allows the frontend to poll for stage updates during background generation.
/// </summary>
public class CharacterTimelineTracker
{
    private readonly ConcurrentDictionary<int, GenerationStatus> _statuses = new();

    public GenerationStatus? GetStatus(int pageId) => _statuses.GetValueOrDefault(pageId);

    public bool TryStart(int pageId)
    {
        var status = new GenerationStatus
        {
            Stage = GenerationStage.Queued,
            Message = "Queued for generation...",
            StartedAt = DateTime.UtcNow,
        };
        return _statuses.TryAdd(pageId, status);
    }

    public void Update(int pageId, GenerationStage stage, string message)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
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

    public void UpdateProgress(int pageId, GenerationStage stage, string message, int currentStep, int totalSteps, string? currentItem = null, int eventsExtracted = 0)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
            {
                Stage = stage,
                Message = message,
                StartedAt = DateTime.UtcNow,
                CurrentStep = currentStep,
                TotalSteps = totalSteps,
                CurrentItem = currentItem,
                EventsExtracted = eventsExtracted,
            },
            (_, existing) =>
            {
                existing.Stage = stage;
                existing.Message = message;
                existing.CurrentStep = currentStep;
                existing.TotalSteps = totalSteps;
                existing.CurrentItem = currentItem;
                existing.EventsExtracted = eventsExtracted;
                return existing;
            }
        );
    }

    public void Complete(int pageId, string message) => Update(pageId, GenerationStage.Complete, message);

    public void Fail(int pageId, string error)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus
            {
                Stage = GenerationStage.Failed,
                Message = "Generation failed",
                Error = error,
                StartedAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Stage = GenerationStage.Failed;
                existing.Message = "Generation failed";
                existing.Error = error;
                return existing;
            }
        );
    }

    public void Clear(int pageId) => _statuses.TryRemove(pageId, out _);

    public void AddActivityLog(int pageId, ActivityLogEntry entry)
    {
        _statuses.AddOrUpdate(
            pageId,
            _ => new GenerationStatus { ActivityLog = [entry] },
            (_, existing) =>
            {
                existing.ActivityLog.Add(entry);
                return existing;
            }
        );
    }

    public bool IsRunning(int pageId) => _statuses.TryGetValue(pageId, out var s) && s.Stage is not (GenerationStage.Complete or GenerationStage.Failed);
}
