using StarWarsData.Models.Queries;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that keeps the chat session list in sync between the nav sidebar and the Ask page.
/// </summary>
public class ChatHistoryService(ApiClient apiClient)
{
    private List<ChatSessionSummary> _sessions = [];
    private bool _loaded;

    public IReadOnlyList<ChatSessionSummary> Sessions => _sessions;

    public Guid? ActiveSessionId { get; set; }

    public event Action? OnChange;

    public async Task LoadAsync()
    {
        try
        {
            var response = await apiClient.GetAsync("api/ChatSessions");
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
            await apiClient.DeleteAsync($"api/ChatSessions/{sessionId}");
            _sessions.RemoveAll(s => s.Id == sessionId);
            if (ActiveSessionId == sessionId) ActiveSessionId = null;
            OnChange?.Invoke();
        }
        catch { }
    }

    public async Task<string> SummarizeTitleAsync(string prompt)
    {
        try
        {
            var response = await apiClient.PostAsJsonAsync("api/ChatSessions/summarize", new { prompt });
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

    public static string FallbackTitle(string prompt)
    {
        var words = prompt.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 4)
            return prompt.Trim();
        return string.Join(' ', words[..4]) + "\u2026";
    }
}
