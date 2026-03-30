using Hangfire.Common;
using Hangfire.Server;
using StarWarsData.Services;

namespace StarWarsData.Admin;

/// <summary>
/// Hangfire server filter that checks JobToggleService before executing a recurring job.
/// If the job is disabled, it cancels the execution silently.
/// </summary>
public class JobToggleFilter(JobToggleService toggleService, ILogger<JobToggleFilter> logger)
    : IServerFilter
{
    public void OnPerforming(PerformingContext context)
    {
        var jobId = context.GetJobParameter<string>("RecurringJobId");
        if (jobId is null) return; // Not a recurring job — let it run

        // Check toggle synchronously (Hangfire filters are sync)
        var enabled = toggleService.IsEnabledAsync(jobId).GetAwaiter().GetResult();
        if (!enabled)
        {
            logger.LogInformation("Job {JobId} is disabled — skipping execution", jobId);
            context.Canceled = true;
        }
    }

    public void OnPerformed(PerformedContext context) { }
}
