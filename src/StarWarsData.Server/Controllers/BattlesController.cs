using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class BattlesController : ControllerBase
{
    private readonly ILogger<BattlesController> _logger;
    private readonly BattleService _service;
    private readonly IHttpContextAccessor _contextAccessor;

    public BattlesController(ILogger<BattlesController> logger, BattleService service, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _service = service;
        _contextAccessor = contextAccessor;
    }
    
    [HttpGet("charts/battle-victories")]
    public async Task<PagedChartData> GetBirthDeaths([FromQuery] QueryParams query)
    {
        return await _service.GetChartData(query.Page, query.PageSize);
    }
}