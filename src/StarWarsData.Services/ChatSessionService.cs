using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;

namespace StarWarsData.Services;

public class ChatSessionService
{
    private readonly IMongoCollection<ChatSession> _sessions;

    public ChatSessionService(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    {
        var db = mongoClient.GetDatabase(settings.Value.ChatSessionsDb);
        _sessions = db.GetCollection<ChatSession>("sessions");
    }

    public async Task<List<ChatSessionSummary>> GetSessionsAsync(
        string userId,
        CancellationToken ct = default
    )
    {
        var sessions = await _sessions
            .Find(s => s.UserId == userId)
            .SortByDescending(s => s.UpdatedAt)
            .Project(s => new ChatSessionSummary
            {
                Id = s.Id,
                Title = s.Title,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
            })
            .ToListAsync(ct);

        return sessions;
    }

    public async Task<ChatSessionDetail?> GetSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken ct = default
    )
    {
        var session = await _sessions
            .Find(s => s.Id == sessionId && s.UserId == userId)
            .FirstOrDefaultAsync(ct);

        if (session is null)
            return null;

        return new ChatSessionDetail
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Messages = session.Messages,
        };
    }

    public async Task<Guid> SaveSessionAsync(
        string userId,
        SaveChatSessionRequest request,
        CancellationToken ct = default
    )
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = request.Title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Messages = request.Messages,
        };

        await _sessions.InsertOneAsync(session, cancellationToken: ct);
        return session.Id;
    }

    public async Task<bool> UpdateSessionAsync(
        string userId,
        Guid sessionId,
        SaveChatSessionRequest request,
        CancellationToken ct = default
    )
    {
        var updateDef = Builders<ChatSession>
            .Update.Set(s => s.Messages, request.Messages)
            .Set(s => s.UpdatedAt, DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(request.Title))
            updateDef = updateDef.Set(s => s.Title, request.Title);

        var update = updateDef;

        var result = await _sessions.UpdateOneAsync(
            s => s.Id == sessionId && s.UserId == userId,
            update,
            cancellationToken: ct
        );

        return result.ModifiedCount > 0;
    }

    public async Task<bool> DeleteSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken ct = default
    )
    {
        var result = await _sessions.DeleteOneAsync(
            s => s.Id == sessionId && s.UserId == userId,
            ct
        );

        return result.DeletedCount > 0;
    }

    /// <summary>
    /// Simple fallback title: first 4 words of the prompt.
    /// </summary>
    public static string FallbackTitle(string prompt)
    {
        var words = prompt.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 4)
            return prompt.Trim();
        return string.Join(' ', words[..4]) + "\u2026";
    }
}
