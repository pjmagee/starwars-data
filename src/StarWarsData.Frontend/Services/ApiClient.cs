using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped wrapper that creates HttpRequestMessages with the Bearer token attached.
/// In Blazor Server, DelegatingHandlers can't access scoped services, so we attach
/// the token per-request from the CircuitTokenProvider.
/// </summary>
public class ApiClient(IHttpClientFactory httpClientFactory, CircuitTokenProvider tokenProvider)
{
    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("StarWarsData");
        if (!string.IsNullOrEmpty(tokenProvider.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenProvider.AccessToken);
        }
        return client;
    }

    public async Task<HttpResponseMessage> GetAsync(string requestUri)
        => await CreateClient().GetAsync(requestUri);

    public async Task<T?> GetFromJsonAsync<T>(string requestUri)
        => await CreateClient().GetFromJsonAsync<T>(requestUri);

    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string requestUri, T value)
        => await CreateClient().PostAsJsonAsync(requestUri, value);

    public async Task<HttpResponseMessage> PutAsJsonAsync<T>(string requestUri, T value)
        => await CreateClient().PutAsJsonAsync(requestUri, value);

    public async Task<HttpResponseMessage> DeleteAsync(string requestUri)
        => await CreateClient().DeleteAsync(requestUri);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(tokenProvider.AccessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenProvider.AccessToken);
        }
        return await httpClientFactory.CreateClient("StarWarsData").SendAsync(request, completionOption, ct);
    }
}
