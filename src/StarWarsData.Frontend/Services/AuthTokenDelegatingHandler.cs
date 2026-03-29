using Microsoft.AspNetCore.Authentication;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// DelegatingHandler that extracts the access token from the current user's
/// authentication cookie and forwards it as a Bearer token to the API service.
/// </summary>
public class AuthTokenDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var accessToken = await httpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
