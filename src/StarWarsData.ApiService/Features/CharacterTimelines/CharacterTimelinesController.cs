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

    public CharacterTimelinesController(CharacterTimelineService service, CharacterTimelineTracker tracker, IServiceScopeFactory scopeFactory, ILogger<CharacterTimelinesController> logger)
    {
        _service = service;
        _tracker = tracker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet("active")]
    public ActionResult<List<ActiveGenerationDto>> GetActiveGenerations()
    {
        var active = _tracker.GetActiveStatuses();
        return Ok(
            active
                .Select(a => new ActiveGenerationDto
                {
                    PageId = a.PageId,
                    CharacterTitle = a.Status.CharacterTitle,
                    Stage = a.Status.Stage,
                    Message = a.Status.Message,
                    CurrentItem = a.Status.CurrentItem,
                    CurrentStep = a.Status.CurrentStep,
                    TotalSteps = a.Status.TotalSteps,
                    EventsExtracted = a.Status.EventsExtracted,
                    StartedAt = a.Status.StartedAt,
                })
                .ToList()
        );
    }

    [HttpGet("list")]
    public async Task<CharacterTimelineListResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        [FromQuery] string? search = null,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? sortDirection = null,
        CancellationToken ct = default
    )
    {
        return await _service.ListTimelinesAsync(page, pageSize, search, continuity, sort, sortDirection, ct);
    }

    [HttpGet("search")]
    public async Task<List<CharacterSearchResult>> Search([FromQuery] string query, [FromQuery] Continuity? continuity = null, CancellationToken ct = default)
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
    public async Task<ActionResult<GenerationStatus>> GetStatus(int pageId, CancellationToken ct)
    {
        var status = _tracker.GetStatus(pageId);
        if (status is not null)
            return Ok(status);

        // No in-memory status — check if there are pending checkpoints from an interrupted run
        var hasPending = await _service.HasPendingCheckpointsAsync(pageId, ct);
        if (hasPending)
        {
            return Ok(
                new GenerationStatus
                {
                    Stage = GenerationStage.Failed,
                    Message = "Generation was interrupted. Click Retry to resume from where it left off.",
                    HasPendingCheckpoint = true,
                    StartedAt = DateTime.UtcNow,
                }
            );
        }

        return NotFound();
    }

    [HttpPost("{pageId:int}/generate")]
    public async Task<IActionResult> Generate(int pageId, CancellationToken ct)
    {
        if (_tracker.IsRunning(pageId))
            return Conflict(new { message = "Generation already in progress" });

        var characterInfo = await _service.GetCharacterInfoAsync(pageId, _tracker, ct);

        if (!_tracker.TryStart(pageId, characterInfo?.Title))
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

public class ActiveGenerationDto
{
    public int PageId { get; set; }
    public string? CharacterTitle { get; set; }
    public GenerationStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? CurrentItem { get; set; }
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public int EventsExtracted { get; set; }
    public DateTime StartedAt { get; set; }
}
