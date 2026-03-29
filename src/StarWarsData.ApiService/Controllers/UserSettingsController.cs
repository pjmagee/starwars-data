using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/user/settings")]
public class UserSettingsController(
    UserSettingsService userSettingsService,
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

    public record SetKeyRequest(string ApiKey);
}
