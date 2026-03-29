using System.Security.Claims;

namespace StarWarsData.ApiService;

public static class UserIdExtensions
{
    /// <summary>
    /// Extracts the user ID (Keycloak 'sub' claim) from the authenticated ClaimsPrincipal.
    /// </summary>
    public static string? GetUserId(this ClaimsPrincipal user)
        => user.FindFirst("sub")?.Value
           ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
