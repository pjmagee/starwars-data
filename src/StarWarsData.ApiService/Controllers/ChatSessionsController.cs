using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatSessionsController : ControllerBase
{
    private readonly ChatSessionService _chatSessionService;

    public ChatSessionsController(ChatSessionService chatSessionService)
    {
        _chatSessionService = chatSessionService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ChatSessionSummary>>> GetSessions(
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        return await _chatSessionService.GetSessionsAsync(userId, ct);
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<ActionResult<ChatSessionDetail>> GetSession(
        Guid sessionId,
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var session = await _chatSessionService.GetSessionAsync(userId, sessionId, ct);
        if (session is null)
            return NotFound();
        return session;
    }

    [HttpPost]
    public async Task<ActionResult<Guid>> SaveSession(
        [FromHeader(Name = "X-User-Id")] string? userId,
        [FromBody] SaveChatSessionRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var id = await _chatSessionService.SaveSessionAsync(userId, request, ct);
        return CreatedAtAction(nameof(GetSession), new { sessionId = id }, id);
    }

    [HttpPut("{sessionId:guid}")]
    public async Task<IActionResult> UpdateSession(
        Guid sessionId,
        [FromHeader(Name = "X-User-Id")] string? userId,
        [FromBody] SaveChatSessionRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var updated = await _chatSessionService.UpdateSessionAsync(userId, sessionId, request, ct);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(
        Guid sessionId,
        [FromHeader(Name = "X-User-Id")] string? userId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var deleted = await _chatSessionService.DeleteSessionAsync(userId, sessionId, ct);
        return deleted ? NoContent() : NotFound();
    }
}
