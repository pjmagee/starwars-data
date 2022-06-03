using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.API.Controllers;

[ApiController]
[Route("[controller]")]
public class TimelineController : ControllerBase
{
    private readonly ILogger<TimelineController> _logger;
    private readonly RecordsService _recordsService;
    private readonly IHttpContextAccessor _contextAccessor;

    public TimelineController(ILogger<TimelineController> logger, RecordsService recordsService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordsService = recordsService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<PagedResult> Get([FromQuery] QueryParams queryParams)
    {
        return await _recordsService.GetTimelineEvents(queryParams?.Page ?? 1, queryParams?.PageSize ?? 50);
    }
}