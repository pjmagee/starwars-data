using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StarWarsData.Services;

/// <summary>
/// Singleton that tracks OpenAI API call outcomes across all services.
/// Records last success/failure per caller, overall health, and quota status.
/// </summary>
public class OpenAiStatusService
{
    readonly ILogger<OpenAiStatusService> _logger;
    readonly ConcurrentDictionary<string, CallerStatus> _callers = new();
    readonly object _lock = new();

    // Rolling window of recent errors for health scoring
    readonly List<(DateTime time, string caller, string error)> _recentErrors = [];

    public OpenAiStatusService(ILogger<OpenAiStatusService> logger)
    {
        _logger = logger;
    }

    public void RecordSuccess(string caller)
    {
        var status = _callers.GetOrAdd(caller, _ => new CallerStatus { Caller = caller });
        status.LastSuccess = DateTime.UtcNow;
        status.TotalSuccesses++;
        status.ConsecutiveErrors = 0;
        status.LastError = null;
        status.LastErrorTime = null;
        status.IsQuotaBlocked = false;
    }

    public void RecordError(string caller, Exception ex)
    {
        var status = _callers.GetOrAdd(caller, _ => new CallerStatus { Caller = caller });
        status.LastError = ex.Message;
        status.LastErrorTime = DateTime.UtcNow;
        status.TotalErrors++;
        status.ConsecutiveErrors++;

        if (IsQuotaError(ex))
        {
            status.IsQuotaBlocked = true;
            status.QuotaBlockedSince ??= DateTime.UtcNow;
        }
        else if (IsRateLimitError(ex))
        {
            status.IsRateLimited = true;
            status.RateLimitedSince ??= DateTime.UtcNow;
        }

        lock (_lock)
        {
            _recentErrors.Add((DateTime.UtcNow, caller, ex.Message));
            // Keep last hour only
            _recentErrors.RemoveAll(e => e.time < DateTime.UtcNow.AddHours(-1));
        }
    }

    public void ClearQuotaBlock(string caller)
    {
        if (_callers.TryGetValue(caller, out var status))
        {
            status.IsQuotaBlocked = false;
            status.QuotaBlockedSince = null;
            status.IsRateLimited = false;
            status.RateLimitedSince = null;
            status.ConsecutiveErrors = 0;
        }
    }

    public OpenAiHealthReport GetHealthReport()
    {
        var callers = _callers.Values.ToList();
        List<(DateTime time, string caller, string error)> errors;
        lock (_lock)
        {
            errors = [.. _recentErrors];
        }

        var anyQuotaBlocked = callers.Any(c => c.IsQuotaBlocked);
        var anyRateLimited = callers.Any(c => c.IsRateLimited);
        var errorsLastHour = errors.Count;

        var overallStatus = anyQuotaBlocked
            ? HealthStatus.QuotaExceeded
            : anyRateLimited
                ? HealthStatus.RateLimited
                : errorsLastHour > 10
                    ? HealthStatus.Degraded
                    : callers.Count == 0
                        ? HealthStatus.Unknown
                        : HealthStatus.Healthy;

        return new OpenAiHealthReport
        {
            Status = overallStatus,
            Callers = callers.OrderBy(c => c.Caller).ToList(),
            ErrorsLastHour = errorsLastHour,
            RecentErrors = errors
                .OrderByDescending(e => e.time)
                .Take(20)
                .Select(e => new RecentError
                {
                    Time = e.time,
                    Caller = e.caller,
                    Message = TruncateError(e.error)
                })
                .ToList()
        };
    }

    static bool IsQuotaError(Exception ex) =>
        ex.Message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("429", StringComparison.Ordinal)
            && ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        || (ex.InnerException != null && IsQuotaError(ex.InnerException));

    static bool IsRateLimitError(Exception ex) =>
        (ex.Message.Contains("429", StringComparison.Ordinal)
         && !ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        || ex.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
        || (ex.InnerException != null && IsRateLimitError(ex.InnerException));

    static string TruncateError(string msg) =>
        msg.Length > 200 ? msg[..200] + "..." : msg;
}

public enum HealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    RateLimited,
    QuotaExceeded
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

public class OpenAiHealthReport
{
    public HealthStatus Status { get; set; }
    public List<CallerStatus> Callers { get; set; } = [];
    public int ErrorsLastHour { get; set; }
    public List<RecentError> RecentErrors { get; set; } = [];
}

public class RecentError
{
    public DateTime Time { get; set; }
    public string Caller { get; set; } = "";
    public string Message { get; set; } = "";
}
