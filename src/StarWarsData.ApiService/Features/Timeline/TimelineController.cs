using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class TimelineController : ControllerBase
{
    readonly ILogger<TimelineController> _logger;
    readonly TimelineService _timelineService;
    readonly RecordService _recordService;

    public TimelineController(ILogger<TimelineController> logger, TimelineService timelineService, RecordService recordService)
    {
        _logger = logger;
        _timelineService = timelineService;
        _recordService = recordService;
    }

    [HttpGet("eras")]
    public async Task<List<Era>> GetEras([FromQuery] Continuity? continuity = null, CancellationToken ct = default) => await _timelineService.GetErasAsync(continuity, ct);

    [HttpGet("events")]
    public async Task<GroupedTimelineResult?> GetTimelineEvents([FromQuery] TimelineQueryParams queryParams)
    {
        var categories = queryParams.Categories;
        return await _timelineService.GetTimelineEvents(
            categories,
            queryParams.Continuity,
            queryParams.Universe,
            queryParams?.Page ?? 1,
            queryParams?.PageSize ?? 50,
            queryParams?.YearFrom,
            queryParams?.YearFromDemarcation,
            queryParams?.YearTo,
            queryParams?.YearToDemarcation,
            queryParams?.Search
        );
    }

    /// <summary>
    /// Returns distinct infobox template names from Pages, filtered by continuity and universe.
    /// </summary>
    [HttpGet("categories")]
    public async Task<List<string>> GetTimelineCategories([FromQuery] Continuity? continuity = null, [FromQuery] Universe? universe = null, CancellationToken cancellationToken = default)
    {
        return await _recordService.GetFilteredCollectionNames(continuity, universe, cancellationToken);
    }

    [HttpGet("categories/{category}/events")]
    public async Task<GroupedTimelineResult?> GetCategoryTimelineEvents(
        string category,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Universe? universe = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50
    )
    {
        return await _timelineService.GetCategoryTimelineEvents(category, continuity, universe, page, pageSize);
    }

    [HttpGet("available-categories")]
    public async Task<IEnumerable<string>> GetAvailableTimelineCategories()
    {
        return await _timelineService.GetTimelineCategories();
    }
}
