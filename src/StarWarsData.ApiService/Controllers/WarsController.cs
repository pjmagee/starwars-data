using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class WarsController : ControllerBase
{
    readonly ILogger<WarsController> _logger;
    readonly WarService _service;
    readonly IHttpContextAccessor _contextAccessor;

    public WarsController(
        ILogger<WarsController> logger, 
        WarService service, 
        IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _service = service;
        _contextAccessor = contextAccessor;
    }
    
    [HttpGet("charts/duration")]
    public async Task<PagedChartData<double>> GetWarsByDuration([FromQuery] QueryParams query) => await _service.GetWarsByDuration(query.Page, query.PageSize);
    
    [HttpGet("charts/battles")]
    public async Task<PagedChartData<int>> GetWarsByBattles([FromQuery] QueryParams query) => await _service.GetWarsByBattles(query.Page, query.PageSize);
}