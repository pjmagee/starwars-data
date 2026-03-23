using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class CharacterTimelinesController : ControllerBase
{
    private readonly CharacterTimelineService _service;
    private readonly CharacterTimelineTracker _tracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CharacterTimelinesController> _logger;

    public CharacterTimelinesController(
        CharacterTimelineService service,
        CharacterTimelineTracker tracker,
        IServiceScopeFactory scopeFactory,
        ILogger<CharacterTimelinesController> logger)
    {
        _service = service;
        _tracker = tracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<List<CharacterSearchResult>> Search(
        [FromQuery] string query,
        [FromQuery] Continuity? continuity = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return await _service.SearchCharactersAsync(query, continuity, _tracker, ct);
    }

    [HttpGet("{pageId:int}")]
    public async Task<ActionResult<CharacterTimeline>> Get(int pageId, CancellationToken ct)
    {
        var timeline = await _service.GetTimelineAsync(pageId, ct);
        if (timeline is null)
            return NotFound();
        return Ok(timeline);
    }

    [HttpGet("{pageId:int}/character")]
    public async Task<ActionResult<CharacterSearchResult>> GetCharacterInfo(int pageId, CancellationToken ct)
    {
        var info = await _service.GetCharacterInfoAsync(pageId, _tracker, ct);
        if (info is null)
            return NotFound();
        return Ok(info);
    }

    [HttpGet("{pageId:int}/status")]
    public ActionResult<GenerationStatus> GetStatus(int pageId)
    {
        var status = _tracker.GetStatus(pageId);
        if (status is null)
            return NotFound();
        return Ok(status);
    }

    [HttpPost("{pageId:int}/generate")]
    public IActionResult Generate(int pageId)
    {
        if (_tracker.IsRunning(pageId))
            return Conflict(new { message = "Generation already in progress" });

        if (!_tracker.TryStart(pageId))
            return Conflict(new { message = "Generation already in progress" });

        // Fire-and-forget with a new DI scope for the background work
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<CharacterTimelineService>();
            try
            {
                await service.GenerateTimelineAsync(pageId, CancellationToken.None, _tracker);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background timeline generation failed for PageId={PageId}", pageId);
                _tracker.Fail(pageId, ex.Message);
            }
        });

        return Accepted();
    }
}
