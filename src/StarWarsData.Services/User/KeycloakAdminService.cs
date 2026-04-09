using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarWarsData.Models;

namespace StarWarsData.Services;

/// <summary>
/// Calls the Keycloak Admin REST API using a service-account client credentials grant.
/// Used for GDPR right-to-erasure (deleting a user from the realm).
/// </summary>
public class KeycloakAdminService(HttpClient httpClient, IOptions<SettingsOptions> settings, ILogger<KeycloakAdminService> logger)
{
    string? _cachedToken;
    DateTime _tokenExpiry = DateTime.MinValue;

    /// <summary>
    /// Deletes a user from Keycloak by their subject ID (the "sub" claim).
    /// Returns true if the user was deleted, false if they were not found.
    /// </summary>
    public async Task<bool> DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{settings.Value.KeycloakBaseUrl}/admin/realms/{settings.Value.KeycloakRealm}/users/{userId}");
        request.Headers.Authorization = new("Bearer", token);

        var response = await httpClient.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Deleted Keycloak user {UserId} from realm {Realm}", userId, settings.Value.KeycloakRealm);
            return true;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("Keycloak user {UserId} not found in realm {Realm} — may already be deleted", userId, settings.Value.KeycloakRealm);
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogError("Failed to delete Keycloak user {UserId}: {Status} {Body}", userId, response.StatusCode, body);
        response.EnsureSuccessStatusCode();
        return false;
    }

    async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        var opts = settings.Value;
        var tokenUrl = $"{opts.KeycloakBaseUrl}/realms/{opts.KeycloakRealm}/protocol/openid-connect/token";

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = opts.KeycloakAdminClientId,
                    ["client_secret"] = opts.KeycloakAdminClientSecret,
                }
            ),
        };

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct) ?? throw new InvalidOperationException("Empty token response from Keycloak");

        _cachedToken = token.AccessToken;
        // Expire 30s early to avoid edge-case failures
        _tokenExpiry = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 30);

        return _cachedToken;
    }

    sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
