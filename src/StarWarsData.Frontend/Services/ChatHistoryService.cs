using Microsoft.JSInterop;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Scoped service that keeps the chat session list in sync between the nav sidebar and the Ask page.
/// Dual-mode: authenticated users persist via API, anonymous users persist via localStorage.
/// </summary>
public class ChatHistoryService(IHttpClientFactory httpClientFactory, IJSRuntime js)
{
    private List<ChatSessionSummary> _sessions = [];
    private bool _loaded;
    private bool _isAuthenticated;

    public IReadOnlyList<ChatSessionSummary> Sessions => _sessions;

    public Guid? ActiveSessionId { get; set; }

    public event Action? OnChange;

    private HttpClient Http => httpClientFactory.CreateClient("StarWarsData");

    public void SetAuthenticated(bool isAuthenticated)
    {
        if (_isAuthenticated != isAuthenticated)
        {
            _isAuthenticated = isAuthenticated;
            _sessions = [];
            _loaded = false;
            ActiveSessionId = null;
            OnChange?.Invoke();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (_isAuthenticated)
            {
                var response = await Http.GetAsync("api/ChatSessions");
                if (response.IsSuccessStatusCode)
                {
                    _sessions = await response.Content.ReadFromJsonAsync<List<ChatSessionSummary>>() ?? [];
                }
            }
            else
            {
                var local = await js.InvokeAsync<List<LocalChatSession>>("swChatLoad");
                _sessions = (local ?? [])
                    .Select(s => new ChatSessionSummary
                    {
                        Id = s.Id,
                        Title = s.Title,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt,
                    })
                    .ToList();
            }
        }
        catch { }
        _loaded = true;
        OnChange?.Invoke();
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
            await LoadAsync();
    }

    public void NotifyChanged() => OnChange?.Invoke();

    public async Task DeleteAsync(Guid sessionId)
    {
        try
        {
            if (_isAuthenticated)
            {
                await Http.DeleteAsync($"api/ChatSessions/{sessionId}");
            }
            else
            {
                await js.InvokeVoidAsync("swChatDelete", sessionId);
            }

            _sessions.RemoveAll(s => s.Id == sessionId);
            if (ActiveSessionId == sessionId)
                ActiveSessionId = null;
            OnChange?.Invoke();
        }
        catch { }
    }

    /// <summary>
    /// Save a session to localStorage (anonymous users) or update the in-memory list after API save.
    /// </summary>
    public async Task SaveLocalSessionAsync(Guid sessionId, string title, List<ChatSessionMessage> messages)
    {
        var session = new LocalChatSession
        {
            Id = sessionId,
            Title = title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Messages = messages,
        };
        await js.InvokeVoidAsync("swChatSave", session);

        // Update in-memory list
        var existing = _sessions.FindIndex(s => s.Id == sessionId);
        var summary = new ChatSessionSummary
        {
            Id = sessionId,
            Title = title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
        };

        if (existing >= 0)
            _sessions[existing] = summary;
        else
            _sessions.Insert(0, summary);

        OnChange?.Invoke();
    }

    /// <summary>
    /// Load a full session from localStorage (anonymous users).
    /// </summary>
    public async Task<LocalChatSession?> LoadLocalSessionAsync(Guid sessionId)
    {
        return await js.InvokeAsync<LocalChatSession?>("swChatLoadOne", sessionId.ToString());
    }

    /// <summary>
    /// Get all local sessions for migration to authenticated storage.
    /// </summary>
    public async Task<List<LocalChatSession>> GetAllLocalSessionsAsync()
    {
        try
        {
            return await js.InvokeAsync<List<LocalChatSession>>("swChatLoad") ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Clear all local sessions (after migration to authenticated storage).
    /// </summary>
    public async Task ClearLocalSessionsAsync()
    {
        try
        {
            await js.InvokeVoidAsync("swChatClear");
        }
        catch { }
    }

    public async Task<string> SummarizeTitleAsync(string prompt)
    {
        try
        {
            var response = await Http.PostAsJsonAsync("api/ChatSessions/summarize", new { prompt });
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

/// <summary>
/// Full chat session stored in localStorage for anonymous users.
/// </summary>
public class LocalChatSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ChatSessionMessage> Messages { get; set; } = [];
}
