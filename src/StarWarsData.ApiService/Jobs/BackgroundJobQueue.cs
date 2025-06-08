using System.Collections.Concurrent;
using StarWarsData.Models.Entities;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.ApiService.Jobs;

public class BackgroundJobQueue : IBackgroundJobQueue
{
    readonly ConcurrentQueue<(Guid id, string name, Func<CancellationToken, Task> work)> _workItems = new();
    readonly ConcurrentDictionary<Guid, JobInfo> _jobs = new();
    readonly IMongoCollection<JobInfo> _jobCollection;

    public BackgroundJobQueue(IOptions<SettingsOptions> options, IMongoClient mongoClient)
    {
        var db = mongoClient.GetDatabase(options.Value.RawDb);
        _jobCollection = db.GetCollection<JobInfo>("jobs");
    }

    public Guid Enqueue(Func<CancellationToken, Task> work, string name)
    {
        // Prevent duplicate pending or running jobs
        var filter = Builders<JobInfo>.Filter.And(
            Builders<JobInfo>.Filter.Eq(j => j.Name, name),
            Builders<JobInfo>.Filter.In(j => j.Status, [JobStatus.Pending, JobStatus.Running])
        );
        
        var exists = _jobCollection.CountDocuments(filter) > 0;
        if (exists) throw new InvalidOperationException($"Job '{name}' is already pending or running.");
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var job = new JobInfo
        {
            Id = id,
            Name = name,
            Status = JobStatus.Pending,
            CreatedAt = now,
            LastUpdatedAt = now
        };
        // Persist job
        _jobCollection.InsertOne(job);

        _jobs[id] = job;
        _workItems.Enqueue((id, name, work));
        return id;
    }

    public bool TryDequeue(out (Guid id, string name, Func<CancellationToken, Task> work) workItem)
    {
        return _workItems.TryDequeue(out workItem);
    }

    public IReadOnlyDictionary<Guid, JobInfo> GetJobs()
    {
        return _jobs;
    }

    public bool TryCancel(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job) && (job.Status == JobStatus.Pending || job.Status == JobStatus.Running))
        {
            var now = DateTime.UtcNow;
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.LastUpdatedAt = now;
            _jobCollection.UpdateOne(j => j.Id == jobId, Builders<JobInfo>.Update
                .Set(j => j.Status, JobStatus.Cancelled)
                .Set(j => j.CompletedAt, now)
                .Set(j => j.LastUpdatedAt, now)
            );
            return true;
        }

        return false;
    }

    public void UpdateJobStatus(Guid jobId, JobStatus status, string? errorMessage = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var now = DateTime.UtcNow;
            job.Status = status;
            job.LastUpdatedAt = now;
            var update = Builders<JobInfo>.Update
                .Set(j => j.Status, status)
                .Set(j => j.LastUpdatedAt, now);
            if (errorMessage != null)
                update = update.Set(j => j.ErrorMessage, errorMessage);
            _jobCollection.UpdateOne(j => j.Id == jobId, update);
        }
    }

    public void SetJobStarted(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var now = DateTime.UtcNow;
            job.Status = JobStatus.Running;
            job.StartedAt = now;
            job.LastUpdatedAt = now;
            _jobCollection.UpdateOne(j => j.Id == jobId, Builders<JobInfo>.Update
                .Set(j => j.Status, JobStatus.Running)
                .Set(j => j.StartedAt, now)
                .Set(j => j.LastUpdatedAt, now)
            );
        }
    }

    public void SetJobCompleted(Guid jobId, JobStatus status, string? errorMessage = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var now = DateTime.UtcNow;
            job.Status = status;
            job.CompletedAt = now;
            job.LastUpdatedAt = now;

            var update = Builders<JobInfo>.Update
                .Set(j => j.Status, status)
                .Set(j => j.CompletedAt, now)
                .Set(j => j.LastUpdatedAt, now);
            if (errorMessage != null)
                update = update.Set(j => j.ErrorMessage, errorMessage);
            _jobCollection.UpdateOne(j => j.Id == jobId, update);
        }
    }
}

