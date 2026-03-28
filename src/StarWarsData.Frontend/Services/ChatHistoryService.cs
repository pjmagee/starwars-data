using StarWarsData.Models.Queries;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that keeps the chat session list in sync between the nav sidebar and the Ask page.
/// </summary>
public class ChatHistoryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private List<ChatSessionSummary> _sessions = [];
    private bool _loaded;

    public ChatHistoryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IReadOnlyList<ChatSessionSummary> Sessions => _sessions;

    public Guid? ActiveSessionId { get; set; }

    public event Action? OnChange;

    public async Task LoadAsync(string userId)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("StarWarsData");
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/ChatSessions");
            request.Headers.Add("X-User-Id", userId);
            var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _sessions = await response.Content.ReadFromJsonAsync<List<ChatSessionSummary>>() ?? [];
            }
        }
        catch { }
        _loaded = true;
        OnChange?.Invoke();
    }

    public async Task EnsureLoadedAsync(string userId)
    {
        if (!_loaded) await LoadAsync(userId);
    }

    public void NotifyChanged() => OnChange?.Invoke();

    public async Task DeleteAsync(string userId, Guid sessionId)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("StarWarsData");
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/ChatSessions/{sessionId}");
            request.Headers.Add("X-User-Id", userId);
            await http.SendAsync(request);
            _sessions.RemoveAll(s => s.Id == sessionId);
            if (ActiveSessionId == sessionId) ActiveSessionId = null;
            OnChange?.Invoke();
        }
        catch { }
    }

    /// <summary>
    /// Call the API to generate a 3-4 word AI summary for a chat title.
    /// Falls back to first few words if the call fails.
    /// </summary>
    public async Task<string> SummarizeTitleAsync(string prompt)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("StarWarsData");
            var response = await http.PostAsJsonAsync("api/ChatSessions/summarize", new { prompt });
            if (response.IsSuccessStatusCode)
            {
                var title = await response.Content.ReadAsStringAsync();
                title = title.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }
        catch { }

        return FallbackTitle(prompt);
    }

    /// <summary>
    /// Simple fallback: first 4 words of the prompt.
    /// </summary>
    public static string FallbackTitle(string prompt)
    {
        var words = prompt.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 4)
            return prompt.Trim();
        return string.Join(' ', words[..4]) + "\u2026";
    }
}
