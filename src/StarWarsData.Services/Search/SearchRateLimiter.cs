using System.Collections.Concurrent;

namespace StarWarsData.Services;

/// <summary>
/// In-memory rate limiter for the semantic search endpoint.
/// Same limits as Ask AI but a separate sliding window.
/// Anonymous: 3 requests per 30 minutes.
/// Authenticated (no BYOK): 10 requests per 30 minutes.
/// Authenticated with BYOK or admin: unlimited (not checked).
/// </summary>
public class SearchRateLimiter
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    const int AnonymousLimit = 3;
    const int AuthenticatedLimit = 10;

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
