using System.Collections.Concurrent;

namespace StarWarsData.Services;

/// <summary>
/// In-memory rate limiter for the Ask AI endpoint.
/// Anonymous: 1 request per 30 minutes.
/// Authenticated (no BYOK): 6 requests per 30 minutes.
/// Authenticated with BYOK: unlimited (not checked).
/// </summary>
public class AskRateLimiter
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    const int AnonymousLimit = 1;
    const int AuthenticatedLimit = 6;

    readonly ConcurrentDictionary<string, List<DateTime>> _requests = new();

    public RateLimitResult TryAcquire(string clientId, bool isAuthenticated)
    {
        var limit = isAuthenticated ? AuthenticatedLimit : AnonymousLimit;
        var now = DateTime.UtcNow;
        var cutoff = now - Window;

        var timestamps = _requests.GetOrAdd(clientId, _ => []);

        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= limit)
            {
                var oldestInWindow = timestamps.Min();
                var retryAfter = oldestInWindow + Window - now;
                return new RateLimitResult(false, limit, 0, retryAfter);
            }

            timestamps.Add(now);
            return new RateLimitResult(true, limit, limit - timestamps.Count, null);
        }
    }

    /// <summary>
    /// Periodic cleanup of stale entries to prevent unbounded growth.
    /// </summary>
    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow - Window;
        foreach (var kvp in _requests)
        {
            lock (kvp.Value)
            {
                kvp.Value.RemoveAll(t => t < cutoff);
                if (kvp.Value.Count == 0)
                    _requests.TryRemove(kvp.Key, out _);
            }
        }
    }
}

public record RateLimitResult(bool Allowed, int Limit, int Remaining, TimeSpan? RetryAfter);
