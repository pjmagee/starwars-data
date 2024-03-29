using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;
using StarWarsData.Services.Mongo;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class TimelineController : ControllerBase
{
    private readonly ILogger<TimelineController> _logger;
    private readonly TimelineService _timelineService;
    private readonly CollectionFilters _collectionFilters;
    private readonly IHttpContextAccessor _contextAccessor;

    public TimelineController(ILogger<TimelineController> logger, TimelineService timelineService, CollectionFilters collectionFilters, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _timelineService = timelineService;
        _collectionFilters = collectionFilters;
        _contextAccessor = contextAccessor;
    }

    [HttpGet("events")]
    public async Task<GroupedTimelineResult?> GetTimelineEvents([FromQuery] TimelineQueryParams queryParams)
    {
        if (queryParams.Categories is null || queryParams.Categories.Length == 0) return null;
        
        return await _timelineService.GetTimelineEvents(queryParams.Categories, queryParams?.Page ?? 1, queryParams?.PageSize ?? 50);
    }

    [HttpGet("categories")]
    public IEnumerable<string> GetTimelineCategories() => _collectionFilters.Keys;
}