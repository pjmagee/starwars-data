using StarWarsData.Models.Queries;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that keeps the chat session list in sync between the nav sidebar and the Ask page.
/// </summary>
public class ChatHistoryService
{
    private readonly ApiClient _apiClient;
    private List<ChatSessionSummary> _sessions = [];
    private bool _loaded;

    public ChatHistoryService(ApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<ChatSessionSummary> Sessions => _sessions;

    public Guid? ActiveSessionId { get; set; }

    public event Action? OnChange;

    public async Task LoadAsync()
    {
        try
        {
            var http = _apiClient.Http;
            var response = await http.GetAsync("api/ChatSessions");
            if (response.IsSuccessStatusCode)
            {
                _sessions = await response.Content.ReadFromJsonAsync<List<ChatSessionSummary>>() ?? [];
            }
        }
        catch { }
        _loaded = true;
        OnChange?.Invoke();
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded) await LoadAsync();
    }

    public void NotifyChanged() => OnChange?.Invoke();

    public async Task DeleteAsync(Guid sessionId)
    {
        try
        {
            var http = _apiClient.Http;
            await http.DeleteAsync($"api/ChatSessions/{sessionId}");
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
            var http = _apiClient.Http;
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
