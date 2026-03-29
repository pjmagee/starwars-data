using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/user/settings")]
public class UserSettingsController(
    UserSettingsService userSettingsService,
    ChatSessionService chatSessionService,
    ByokChatClient byokChatClient
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> GetSettings(
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var settings = await userSettingsService.GetAsync(userId, ct);
        return Ok(new
        {
            hasOpenAiKey = settings?.OpenAiKeySet == true,
            updatedAt = settings?.UpdatedAt,
        });
    }

    [HttpPut("openai-key")]
    public async Task<ActionResult> SetOpenAiKey(
        [FromHeader(Name = "X-User-Id")] string? userId,
        [FromBody] SetKeyRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.ApiKey) || !request.ApiKey.StartsWith("sk-"))
            return BadRequest(new { error = "Invalid OpenAI API key format. Keys should start with 'sk-'." });

        await userSettingsService.SetOpenAiKeyAsync(userId, request.ApiKey, ct);
        byokChatClient.InvalidateClient(userId);

        return Ok(new { hasOpenAiKey = true });
    }

    [HttpDelete("openai-key")]
    public async Task<ActionResult> RemoveOpenAiKey(
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        await userSettingsService.RemoveOpenAiKeyAsync(userId, ct);
        byokChatClient.InvalidateClient(userId);

        return Ok(new { hasOpenAiKey = false });
    }

    /// <summary>
    /// GDPR right to erasure — deletes all user data (settings, BYOK key, chat sessions).
    /// </summary>
    [HttpDelete("all-data")]
    public async Task<ActionResult> DeleteAllUserData(
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        byokChatClient.InvalidateClient(userId);
        await userSettingsService.DeleteAllUserDataAsync(userId, ct);
        var sessionsDeleted = await chatSessionService.DeleteAllUserDataAsync(userId, ct);

        return Ok(new { deleted = true, sessionsDeleted });
    }

    /// <summary>
    /// GDPR right of access — exports all user data as a JSON file.
    /// </summary>
    [HttpGet("export")]
    public async Task<ActionResult> ExportUserData(
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var settings = await userSettingsService.GetAsync(userId, ct);
        var sessions = await chatSessionService.GetAllSessionsAsync(userId, ct);

        var export = new
        {
            exportedAt = DateTime.UtcNow,
            userId,
            settings = settings is null ? null : new
            {
                hasOpenAiKey = settings.OpenAiKeySet,
                encryptedOpenAiKey = settings.EncryptedOpenAiKey,
                createdAt = settings.CreatedAt,
                updatedAt = settings.UpdatedAt,
            },
            chatSessions = sessions.Select(s => new
            {
                s.Id,
                s.Title,
                s.CreatedAt,
                s.UpdatedAt,
                messages = s.Messages.Select(m => new
                {
                    m.Role,
                    m.Content,
                    m.Timestamp,
                    m.ToolName,
                    m.VisualizationType,
                }),
            }),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        return File(json, "application/json", $"swdata-export-{DateTime.UtcNow:yyyy-MM-dd}.json");
    }

    public record SetKeyRequest(string ApiKey);
}
