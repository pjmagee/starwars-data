using StarWarsData.Models.Entities;

namespace StarWarsData.ApiService.Jobs;

public interface IBackgroundJobQueue
{
    Guid Enqueue(Func<CancellationToken, Task> work, string name);
    bool TryDequeue(out (Guid id, string name, Func<CancellationToken, Task> work) workItem);
    IReadOnlyDictionary<Guid, JobInfo> GetJobs();
    bool TryCancel(Guid jobId);
    void UpdateJobStatus(Guid jobId, JobStatus status, string? errorMessage = null);
    void SetJobStarted(Guid jobId);
    void SetJobCompleted(Guid jobId, JobStatus status, string? errorMessage = null);    }