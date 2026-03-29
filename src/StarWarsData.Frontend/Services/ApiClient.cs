using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped wrapper that attaches the Bearer token to every API request.
///
/// Reads the token from two sources:
/// 1. CircuitTokenProvider (populated after first successful read)
/// 2. HttpContext.GetTokenAsync (available during SSR only)
///
/// Once the token is captured, it's stored in CircuitTokenProvider for
/// subsequent SignalR-based calls where HttpContext is unavailable.
/// </summary>
public class ApiClient(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    CircuitTokenProvider tokenProvider
)
{
    private async Task<string?> GetTokenAsync()
    {
        // Already captured — use it
        if (!string.IsNullOrEmpty(tokenProvider.AccessToken))
            return tokenProvider.AccessToken;

        // Try to capture from HttpContext (only works during SSR)
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var token = await httpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(token))
            {
                tokenProvider.AccessToken = token;
                return token;
            }
        }

        return null;
    }

    private async Task<HttpClient> CreateClientAsync()
    {
        var client = httpClientFactory.CreateClient("StarWarsData");
        var token = await GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    public async Task<HttpResponseMessage> GetAsync(string requestUri)
        => await (await CreateClientAsync()).GetAsync(requestUri);

    public async Task<T?> GetFromJsonAsync<T>(string requestUri)
        => await (await CreateClientAsync()).GetFromJsonAsync<T>(requestUri);

    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string requestUri, T value)
        => await (await CreateClientAsync()).PostAsJsonAsync(requestUri, value);

    public async Task<HttpResponseMessage> PutAsJsonAsync<T>(string requestUri, T value)
        => await (await CreateClientAsync()).PutAsJsonAsync(requestUri, value);

    public async Task<HttpResponseMessage> DeleteAsync(string requestUri)
        => await (await CreateClientAsync()).DeleteAsync(requestUri);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var token = await GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        return await httpClientFactory.CreateClient("StarWarsData")
            .SendAsync(request, completionOption, ct);
    }
}
