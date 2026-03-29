using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChatSessionsController(ChatSessionService chatSessionService, IChatClient chatClient)
    : ControllerBase
{

    [HttpGet]
    public async Task<ActionResult<List<ChatSessionSummary>>> GetSessions(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();
        return await chatSessionService.GetSessionsAsync(userId, ct);
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<ActionResult<ChatSessionDetail>> GetSession(Guid sessionId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var session = await chatSessionService.GetSessionAsync(userId, sessionId, ct);
        if (session is null) return NotFound();
        return session;
    }

    [HttpPost]
    public async Task<ActionResult<Guid>> SaveSession(
        [FromBody] SaveChatSessionRequest request,
        CancellationToken ct
    )
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var id = await chatSessionService.SaveSessionAsync(userId, request, ct);
        return CreatedAtAction(nameof(GetSession), new { sessionId = id }, id);
    }

    [HttpPut("{sessionId:guid}")]
    public async Task<IActionResult> UpdateSession(
        Guid sessionId,
        [FromBody] SaveChatSessionRequest request,
        CancellationToken ct
    )
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var updated = await chatSessionService.UpdateSessionAsync(userId, sessionId, request, ct);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid sessionId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var deleted = await chatSessionService.DeleteSessionAsync(userId, sessionId, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("summarize")]
    [AllowAnonymous]
    public async Task<ActionResult<string>> SummarizeTitle(
        [FromBody] SummarizeRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest();

        try
        {
            var result = await chatClient.GetResponseAsync(
                [
                    new(ChatRole.System,
                        "Summarize the user's message into exactly 3-4 words as a short chat title. "
                        + "No quotes, no punctuation, just the topic. Examples: "
                        + "'Skywalker Family Tree', 'Clone Wars Battles', 'Sith Apprentice Lineage', 'Yoda Species Info'"),
                    new(ChatRole.User, request.Prompt),
                ],
                cancellationToken: ct
            );
            var title = result.Text?.Trim().Trim('"', '.') ?? request.Prompt;
            return Ok(title);
        }
        catch
        {
            return Ok(ChatSessionService.FallbackTitle(request.Prompt));
        }
    }
}

public record SummarizeRequest(string Prompt);
