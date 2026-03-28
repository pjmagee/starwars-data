namespace StarWarsData.Frontend.Models;

public enum HealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    RateLimited,
    QuotaExceeded
}

public class OpenAiHealthReport
{
    public HealthStatus Status { get; set; }
    public List<CallerStatus> Callers { get; set; } = [];
    public int ErrorsLastHour { get; set; }
    public List<RecentError> RecentErrors { get; set; } = [];
}

public class CallerStatus
{
    public string Caller { get; set; } = "";
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastErrorTime { get; set; }
    public string? LastError { get; set; }
    public long TotalSuccesses { get; set; }
    public long TotalErrors { get; set; }
    public int ConsecutiveErrors { get; set; }
    public bool IsQuotaBlocked { get; set; }
    public DateTime? QuotaBlockedSince { get; set; }
    public bool IsRateLimited { get; set; }
    public DateTime? RateLimitedSince { get; set; }
}

public class RecentError
{
    public DateTime Time { get; set; }
    public string Caller { get; set; } = "";
    public string Message { get; set; } = "";
}
