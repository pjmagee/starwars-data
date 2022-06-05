using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class TimelineController : ControllerBase
{
    private readonly ILogger<TimelineController> _logger;
    private readonly RecordsService _recordsService;
    private readonly CollectionFilters _collectionFilters;
    private readonly IHttpContextAccessor _contextAccessor;

    public TimelineController(ILogger<TimelineController> logger, RecordsService recordsService, CollectionFilters collectionFilters, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordsService = recordsService;
        _collectionFilters = collectionFilters;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<GroupedTimelineResult> Get([FromQuery] TimelineQueryParams queryParams)
    {
        return await _recordsService.GetTimelineEvents(queryParams.Categories, queryParams?.Page ?? 1, queryParams?.PageSize ?? 50);
    }

    [HttpGet("categories")]
    public async Task<IEnumerable<string>> GetTimelineCategories()
    {
        // Not every category has dates for ABY/BBY format
        // return await _recordsService.GetCollections(CancellationToken.None);
        return _collectionFilters.Keys;
    }
}