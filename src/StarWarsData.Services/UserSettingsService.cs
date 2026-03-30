using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class UserSettingsService
{
    readonly IMongoCollection<UserSettings> _collection;
    readonly IDataProtector _protector;
    readonly ILogger<UserSettingsService> _logger;

    public UserSettingsService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        IDataProtectionProvider dataProtection,
        ILogger<UserSettingsService> logger
    )
    {
        _collection = mongoClient
            .GetDatabase(settings.Value.DatabaseName)
            .GetCollection<UserSettings>(Collections.UserSettings);
        _protector = dataProtection.CreateProtector("UserSettings.OpenAiKey");
        _logger = logger;
    }

    public async Task<bool> HasOpenAiKeyAsync(string userId, CancellationToken ct = default)
    {
        var settings = await _collection
            .Find(s => s.UserId == userId)
            .FirstOrDefaultAsync(ct);
        return settings?.OpenAiKeySet == true;
    }

    public async Task<string?> GetDecryptedOpenAiKeyAsync(string userId, CancellationToken ct = default)
    {
        var settings = await _collection
            .Find(s => s.UserId == userId)
            .FirstOrDefaultAsync(ct);

        if (settings?.EncryptedOpenAiKey is null)
            return null;

        try
        {
            return _protector.Unprotect(settings.EncryptedOpenAiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt OpenAI key for user {UserId}", userId);
            return null;
        }
    }

    public async Task SetOpenAiKeyAsync(string userId, string apiKey, CancellationToken ct = default)
    {
        var encrypted = _protector.Protect(apiKey);
        var now = DateTime.UtcNow;

        await _collection.UpdateOneAsync(
            s => s.UserId == userId,
            Builders<UserSettings>.Update
                .Set(s => s.EncryptedOpenAiKey, encrypted)
                .Set(s => s.OpenAiKeySet, true)
                .Set(s => s.UpdatedAt, now)
                .SetOnInsert(s => s.CreatedAt, now)
                .SetOnInsert(s => s.UserId, userId),
            new UpdateOptions { IsUpsert = true },
            ct
        );
    }

    public async Task RemoveOpenAiKeyAsync(string userId, CancellationToken ct = default)
    {
        await _collection.UpdateOneAsync(
            s => s.UserId == userId,
            Builders<UserSettings>.Update
                .Set(s => s.EncryptedOpenAiKey, (string?)null)
                .Set(s => s.OpenAiKeySet, false)
                .Set(s => s.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct
        );
    }

    public async Task DeleteAllUserDataAsync(string userId, CancellationToken ct = default)
    {
        await _collection.DeleteOneAsync(s => s.UserId == userId, ct);
    }

    public async Task<UserSettings?> GetAsync(string userId, CancellationToken ct = default)
    {
        return await _collection
            .Find(s => s.UserId == userId)
            .FirstOrDefaultAsync(ct);
    }
}
