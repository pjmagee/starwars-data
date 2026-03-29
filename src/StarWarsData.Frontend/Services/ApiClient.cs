using System.Net.Http.Headers;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped wrapper around the named HttpClient that attaches the user's access token
/// to every request. In Blazor Server, DelegatingHandlers can't reliably access
/// scoped services (they're created in a different DI scope by IHttpClientFactory),
/// so we set the Authorization header directly before each call.
/// </summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly CircuitTokenProvider _tokenProvider;

    public ApiClient(IHttpClientFactory httpClientFactory, CircuitTokenProvider tokenProvider)
    {
        _http = httpClientFactory.CreateClient("StarWarsData");
        _tokenProvider = tokenProvider;
    }

    public HttpClient Http
    {
        get
        {
            if (!string.IsNullOrEmpty(_tokenProvider.AccessToken))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _tokenProvider.AccessToken);
            }
            else
            {
                _http.DefaultRequestHeaders.Authorization = null;
            }
            return _http;
        }
    }
}
