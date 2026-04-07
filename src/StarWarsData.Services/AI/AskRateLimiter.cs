using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StarWarsData.Models;

namespace StarWarsData.Services;

/// <summary>
/// In-memory rate limiter for the Ask AI endpoint.
/// Limits are configured via Settings:RateLimitAnonymous, Settings:RateLimitAuthenticated,
/// and Settings:RateLimitWindowMinutes in appsettings.json. Set to 0 for unlimited.
/// </summary>
public class AskRateLimiter(IOptions<SettingsOptions> options)
{
    private readonly int _anonymousLimit = options.Value.RateLimitAnonymous;
    private readonly int _authenticatedLimit = options.Value.RateLimitAuthenticated;
    private readonly TimeSpan _window = TimeSpan.FromMinutes(options.Value.RateLimitWindowMinutes);

    private readonly ConcurrentDictionary<string, List<DateTime>> _requests = new();

    public RateLimitResult TryAcquire(string clientId, bool isAuthenticated)
    {
        var limit = isAuthenticated ? _authenticatedLimit : _anonymousLimit;

        // 0 = unlimited
        if (limit <= 0)
            return new RateLimitResult(true, 0, 0, null);

        var now = DateTime.UtcNow;
        var cutoff = now - _window;

        var timestamps = _requests.GetOrAdd(clientId, _ => []);

        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= limit)
            {
                var oldestInWindow = timestamps.Min();
                var retryAfter = oldestInWindow + _window - now;
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
        var cutoff = DateTime.UtcNow - _window;
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
