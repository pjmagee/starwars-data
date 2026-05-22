using System.Net;
using System.Text.Json;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Thrown when the API rate-limit middleware returns a 429 for /kernel/stream or
/// /copilot/stream. Carries the parsed body fields so the UI can render the rich
/// "X of Y requests used, retry in N minutes" message instead of a generic
/// "rate limited" string.
///
/// Subclasses <see cref="HttpRequestException"/> with StatusCode = 429 so existing
/// catch clauses that match by status code keep working.
/// </summary>
public sealed class RateLimitedException(int? limit, bool? isAuthenticated, int? retryAfterSeconds) : HttpRequestException("Rate limit exceeded", inner: null, HttpStatusCode.TooManyRequests)
{
    public int? Limit { get; } = limit;
    public bool? IsAuthenticated { get; } = isAuthenticated;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// DelegatingHandler that converts a 429 response into a <see cref="RateLimitedException"/>
/// carrying the parsed JSON body. AGUIChatClient calls <c>EnsureSuccessStatusCode()</c>
/// internally, which throws a bare <see cref="HttpRequestException"/> and discards the
/// response body — by intercepting at the handler layer we keep the body fields the
/// API service emits (limit, isAuthenticated, retryAfterSeconds).
/// </summary>
public sealed class RateLimitMessageHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
            return response;

        int? limit = null;
        int? retryAfterSeconds = null;
        bool? isAuthenticated = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                    limit = l.GetInt32();
                if (root.TryGetProperty("retryAfterSeconds", out var r) && r.ValueKind == JsonValueKind.Number)
                    retryAfterSeconds = r.GetInt32();
                if (root.TryGetProperty("isAuthenticated", out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False))
                    isAuthenticated = a.GetBoolean();
            }
        }
        catch (JsonException) { }
        finally
        {
            response.Dispose();
        }

        if (retryAfterSeconds is null && response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var seconds))
                retryAfterSeconds = seconds;
        }

        throw new RateLimitedException(limit, isAuthenticated, retryAfterSeconds);
    }
}
