using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;

namespace StarWarsData.ApiService.Jobs;

public class BackgroundJobHostedService : BackgroundService
{
    readonly IBackgroundJobQueue _jobQueue;
    readonly ILogger<BackgroundJobHostedService> _logger;

    public BackgroundJobHostedService(
        ILogger<BackgroundJobHostedService> logger,
        IBackgroundJobQueue jobQueue)
    {
        _jobQueue = jobQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_jobQueue.TryDequeue(out var workItem))
            {
                var (id, name, work) = workItem;

                // Set job as started and begin periodic pings to refresh lastUpdatedAt
                _jobQueue.SetJobStarted(id);
                using var pingCts = new CancellationTokenSource();
                
                var pingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!pingCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), pingCts.Token);
                            _jobQueue.UpdateJobStatus(id, JobStatus.Running);
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                try
                {
                    await work(stoppingToken);
                    _jobQueue.SetJobCompleted(id, JobStatus.Completed);
                    _logger.LogInformation("Job {JobId} ({JobName}) completed successfully", id, name);
                }
                catch (OperationCanceledException)
                {
                    _jobQueue.SetJobCompleted(id, JobStatus.Cancelled);
                    _logger.LogInformation("Job {JobId} ({JobName}) was cancelled", id, name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} ({JobName}) failed", id, name);
                    _jobQueue.SetJobCompleted(id, JobStatus.Failed, ex.Message);
                }
                finally
                {
                    await pingCts.CancelAsync();
                    try { await pingTask; }
                    catch
                    {
                        // ignored
                    }
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
