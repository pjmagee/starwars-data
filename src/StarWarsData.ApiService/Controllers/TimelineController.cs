using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class TimelineController : ControllerBase
{
    readonly ILogger<TimelineController> _logger;
    readonly TimelineService _timelineService;
    readonly CollectionFilters _collectionFilters;
    readonly IHttpContextAccessor _contextAccessor;

    public TimelineController(
        ILogger<TimelineController> logger, 
        TimelineService timelineService, 
        CollectionFilters collectionFilters, 
        IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _timelineService = timelineService;
        _collectionFilters = collectionFilters;
        _contextAccessor = contextAccessor;
    }

    [HttpGet("events")]
    public async Task<GroupedTimelineResult?> GetTimelineEvents([FromQuery] TimelineQueryParams queryParams)
    {
        // Let the service handle an empty or null category list (treat as "all categories")
        var categories = queryParams.Categories;
        return await _timelineService.GetTimelineEvents(categories, queryParams?.Page ?? 1, queryParams?.PageSize ?? 50);
    }

    [HttpGet("categories")]
    public async Task<IEnumerable<string>> GetTimelineCategories()
    {
        return await _timelineService.GetDistinctTemplatesAsync();
    }
}

