using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/user/settings")]
[Authorize]
public class UserSettingsController(
    UserSettingsService userSettingsService,
    ChatSessionService chatSessionService,
    ByokChatClient byokChatClient
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> GetSettings(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var settings = await userSettingsService.GetAsync(userId, ct);
        return Ok(new
        {
            hasOpenAiKey = settings?.OpenAiKeySet == true,
            updatedAt = settings?.UpdatedAt,
        });
    }

    [HttpPut("openai-key")]
    public async Task<ActionResult> SetOpenAiKey(
        [FromBody] SetKeyRequest request,
        CancellationToken ct
    )
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.ApiKey) || !request.ApiKey.StartsWith("sk-"))
            return BadRequest(new { error = "Invalid OpenAI API key format. Keys should start with 'sk-'." });

        await userSettingsService.SetOpenAiKeyAsync(userId, request.ApiKey, ct);
        byokChatClient.InvalidateClient(userId);

        return Ok(new { hasOpenAiKey = true });
    }

    [HttpDelete("openai-key")]
    public async Task<ActionResult> RemoveOpenAiKey(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        await userSettingsService.RemoveOpenAiKeyAsync(userId, ct);
        byokChatClient.InvalidateClient(userId);

        return Ok(new { hasOpenAiKey = false });
    }

    /// <summary>
    /// GDPR right to erasure — deletes all user data (settings, BYOK key, chat sessions).
    /// </summary>
    [HttpDelete("all-data")]
    public async Task<ActionResult> DeleteAllUserData(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        byokChatClient.InvalidateClient(userId);
        await userSettingsService.DeleteAllUserDataAsync(userId, ct);
        var sessionsDeleted = await chatSessionService.DeleteAllUserDataAsync(userId, ct);

        return Ok(new { deleted = true, sessionsDeleted });
    }

    public record SetKeyRequest(string ApiKey);
}
