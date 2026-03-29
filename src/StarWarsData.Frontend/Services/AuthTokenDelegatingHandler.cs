using Microsoft.AspNetCore.Authentication;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// DelegatingHandler that forwards the access token as a Bearer header to the API service.
/// Reads from CircuitTokenProvider (captured during initial load) first, then falls back
/// to HttpContext.GetTokenAsync for SSR requests.
/// </summary>
public class AuthTokenDelegatingHandler(
    IHttpContextAccessor httpContextAccessor,
    CircuitTokenProvider tokenProvider
) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var accessToken = tokenProvider.AccessToken;

        if (string.IsNullOrEmpty(accessToken))
        {
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext is not null)
            {
                accessToken = await httpContext.GetTokenAsync("access_token");
            }
        }

        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
