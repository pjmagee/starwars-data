using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace StarWarsData.Services;

/// <summary>
/// IChatClient implementation that swaps the underlying OpenAI client to a user's BYOK key
/// when one is available for the current request. Falls back to the server's key otherwise.
/// Sits at the innermost layer of the ChatClient pipeline so that function invocation,
/// telemetry, and guardrails still work regardless of which key is used.
/// </summary>
public sealed class ByokChatClient : IChatClient
{
    readonly IChatClient _serverClient;
    readonly IHttpContextAccessor _httpContextAccessor;
    readonly UserSettingsService _userSettingsService;
    readonly Func<string, IChatClient> _clientFactory;
    readonly ILogger _logger;

    // Cache BYOK clients per user to avoid creating new HttpClient instances per request
    readonly ConcurrentDictionary<string, IChatClient> _byokClients = new();

    /// <param name="serverClient">The default server ChatClient (used when no BYOK key).</param>
    /// <param name="clientFactory">Factory that creates an IChatClient from an API key string.</param>
    public ByokChatClient(
        IChatClient serverClient,
        IHttpContextAccessor httpContextAccessor,
        UserSettingsService userSettingsService,
        Func<string, IChatClient> clientFactory,
        ILogger logger
    )
    {
        _serverClient = serverClient;
        _httpContextAccessor = httpContextAccessor;
        _userSettingsService = userSettingsService;
        _clientFactory = clientFactory;
        _logger = logger;
    }



    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var client = await ResolveClientAsync(cancellationToken);
        return await client.GetResponseAsync(chatMessages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var client = await ResolveClientAsync(cancellationToken);
        await foreach (var update in client.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _serverClient.GetService(serviceType, serviceKey);

    public void Dispose() { }

    async Task<IChatClient> ResolveClientAsync(CancellationToken ct)
    {
        var userId = _httpContextAccessor.HttpContext?.Request.Headers["X-User-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(userId))
            return _serverClient;

        var hasByok = _httpContextAccessor.HttpContext?.Items["HasByok"] as bool?;
        if (hasByok != true)
            return _serverClient;

        // Check cache first
        if (_byokClients.TryGetValue(userId, out var cached))
            return cached;

        var apiKey = await _userSettingsService.GetDecryptedOpenAiKeyAsync(userId, ct);
        if (apiKey is null)
            return _serverClient;

        var client = _clientFactory(apiKey);

        _byokClients.TryAdd(userId, client);
        _logger.LogInformation("Created BYOK ChatClient for user {UserId}", userId);
        return client;
    }

    /// <summary>
    /// Invalidate a user's cached BYOK client (e.g. when they change or remove their key).
    /// </summary>
    public void InvalidateClient(string userId)
    {
        _byokClients.TryRemove(userId, out _);
    }
}
