using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;

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
    
    [HttpGet("charts/victories")]
    public async Task<PagedChartData<int>> GetBirthsDeathsByYear([FromQuery] QueryParams query) => await _service.GetBattlesByYear(query.Page, query.PageSize);
}