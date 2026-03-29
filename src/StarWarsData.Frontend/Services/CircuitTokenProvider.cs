namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that caches the access token for the Blazor circuit lifetime.
/// Populated by ApiClient on first successful token retrieval from HttpContext.
/// </summary>
public class CircuitTokenProvider
{
    public string? AccessToken { get; set; }
}
