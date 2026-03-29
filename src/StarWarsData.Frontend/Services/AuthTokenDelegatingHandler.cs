using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// DelegatingHandler that forwards the OIDC access token as a Bearer header.
/// Uses IHttpContextAccessor which works across DI scopes via AsyncLocal.
/// With prerendering enabled, the HttpContext from the initial request remains
/// accessible throughout the Blazor circuit lifetime.
/// </summary>
public class AuthTokenDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var accessToken = await httpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
