using System.Collections.Concurrent;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Singleton token store keyed by HTTP connection ID.
/// The middleware populates it during SSR; the scoped CircuitTokenAccessor
/// reads from it using the connection ID captured during the same SSR pass.
/// </summary>
public class TokenStore
{
    readonly ConcurrentDictionary<string, string> _tokens = new();

    public void Set(string connectionId, string token) => _tokens[connectionId] = token;

    public string? Get(string connectionId) => _tokens.TryGetValue(connectionId, out var t) ? t : null;

    public void Remove(string connectionId) => _tokens.TryRemove(connectionId, out _);
}

/// <summary>
/// Scoped service that captures the connection ID during SSR and reads
/// the access token from the singleton TokenStore.
/// </summary>
public class CircuitTokenProvider
{
    public string? AccessToken { get; set; }
}
