using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class BattlesController : ControllerBase
{
    readonly ILogger<BattlesController> _logger;
    readonly BattleService _service;
    readonly IHttpContextAccessor _contextAccessor;

    public BattlesController(
        ILogger<BattlesController> logger, 
        BattleService service, 
        IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _service = service;
        _contextAccessor = contextAccessor;
    }
    
    [HttpGet("charts/victories")]
    public async Task<PagedChartData<int>> GetBirthsDeathsByYear([FromQuery] QueryParams query) => await _service.GetBattlesByYear(query.Page, query.PageSize);
}