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
            queryParams.Realm,
            queryParams?.Page ?? 1,
            queryParams?.PageSize ?? 50,
            queryParams?.YearFrom,
            queryParams?.YearFromDemarcation,
            queryParams?.YearTo,
            queryParams?.YearToDemarcation,
            queryParams?.Search,
            queryParams?.Calendar
        );
    }

    /// <summary>
    /// Returns distinct infobox template names from Pages, filtered by continuity and realm.
    /// </summary>
    [HttpGet("categories")]
    public async Task<List<string>> GetTimelineCategories([FromQuery] Continuity? continuity = null, [FromQuery] Realm? realm = null, CancellationToken cancellationToken = default)
    {
        return await _recordService.GetFilteredCollectionNames(continuity, realm, cancellationToken);
    }

    [HttpGet("categories/{category}/events")]
    public async Task<GroupedTimelineResult?> GetCategoryTimelineEvents(
        string category,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Realm? realm = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50
    )
    {
        return await _timelineService.GetCategoryTimelineEvents(category, continuity, realm, page, pageSize);
    }

    [HttpGet("available-categories")]
    public async Task<IEnumerable<string>> GetAvailableTimelineCategories()
    {
        return await _timelineService.GetTimelineCategories();
    }

    /// <summary>
    /// Resolves a knowledge-graph node's temporal anchor for the /timeline/{nodeId} page.
    /// 404 when the node has no usable lifecycle range. See Design-014 Phase 2.
    /// </summary>
    [HttpGet("anchor/{nodeId:int}")]
    public async Task<ActionResult<NodeAnchor>> GetNodeAnchor(int nodeId, CancellationToken ct = default)
    {
        var anchor = await _timelineService.GetNodeAnchorAsync(nodeId, ct);
        return anchor is null ? NotFound() : Ok(anchor);
    }
}
