using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;

namespace StarWarsData.Frontend.Services;

public class AuthTokenDelegatingHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuthTokenDelegatingHandler> logger
) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            logger.LogWarning("HttpContext is null — cannot attach token to {Url}", request.RequestUri);
        }
        else
        {
            var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
            logger.LogDebug("HttpContext available. Authenticated: {IsAuth}, User: {User}",
                isAuthenticated, httpContext.User.Identity?.Name);

            if (isAuthenticated)
            {
                var accessToken = await httpContext.GetTokenAsync("access_token");
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", accessToken);
                    logger.LogDebug("Attached Bearer token ({Length} chars) to {Url}",
                        accessToken.Length, request.RequestUri);
                }
                else
                {
                    // Check what tokens ARE available
                    var idToken = await httpContext.GetTokenAsync("id_token");
                    var refreshToken = await httpContext.GetTokenAsync("refresh_token");
                    logger.LogWarning(
                        "No access_token found. id_token={HasId}, refresh_token={HasRefresh}. Request: {Url}",
                        idToken is not null, refreshToken is not null, request.RequestUri);
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
