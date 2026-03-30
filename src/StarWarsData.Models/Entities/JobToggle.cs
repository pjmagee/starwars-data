using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Stores the enabled/disabled state and schedule for a Hangfire recurring job.
/// Checked at the start of each job execution — if disabled, the job exits silently.
/// </summary>
public class JobToggle
{
    /// <summary>The Hangfire recurring job ID (e.g. "daily-incremental-sync").</summary>
    [BsonId]
    public string JobId { get; set; } = string.Empty;

    [BsonElement("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Cron expression for the job schedule (e.g. "0 3 * * *").</summary>
    [BsonElement("schedule")]
    public string? Schedule { get; set; }

    /// <summary>Human-readable description of what this job does.</summary>
    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
