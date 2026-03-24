using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace StarWarsData.Frontend.Services;

public sealed class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        // Avoid duplicating roles if called more than once
        if (identity.HasClaim(c => c.Type == "roles"))
            return Task.FromResult(principal);

        // Realm roles
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (!string.IsNullOrWhiteSpace(realmAccess))
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (
                doc.RootElement.TryGetProperty("roles", out var rolesElement)
                && rolesElement.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var role in rolesElement.EnumerateArray())
                {
                    var value = role.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        identity.AddClaim(new Claim("roles", value));
                    }
                }
            }
        }

        // Example: client roles for a specific Keycloak client
        var resourceAccess = principal.FindFirst("resource_access")?.Value;
        if (!string.IsNullOrWhiteSpace(resourceAccess))
        {
            using var doc = JsonDocument.Parse(resourceAccess);
            if (
                doc.RootElement.TryGetProperty("my-client", out var clientElement)
                && clientElement.TryGetProperty("roles", out var rolesElement)
                && rolesElement.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var role in rolesElement.EnumerateArray())
                {
                    var value = role.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        identity.AddClaim(new Claim("roles", value));
                    }
                }
            }
        }

        return Task.FromResult(principal);
    }
}
