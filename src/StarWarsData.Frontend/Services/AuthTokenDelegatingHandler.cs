using System.Security.Claims;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// DelegatingHandler that injects the X-User-Id header from the authenticated
/// user's claims into outgoing API requests. The API is internal-only (not exposed
/// to the internet) so identity is trusted from the Blazor Server frontend.
///
/// Uses IHttpContextAccessor which works via AsyncLocal — the HttpContext from the
/// initial prerender request remains accessible throughout the Blazor circuit.
/// </summary>
public class UserIdDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated == true)
        {
            var userId = httpContext.User.FindFirst("sub")?.Value
                         ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(userId))
            {
                request.Headers.Remove("X-User-Id");
                request.Headers.Add("X-User-Id", userId);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
