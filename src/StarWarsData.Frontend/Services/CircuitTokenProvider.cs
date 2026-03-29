namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that captures the access token during the initial HTTP request
/// and makes it available throughout the Blazor Server circuit lifetime.
/// HttpContext is only available during the initial request, not during SignalR
/// callbacks, so tokens must be captured eagerly.
/// </summary>
public class CircuitTokenProvider
{
    public string? AccessToken { get; set; }
}
