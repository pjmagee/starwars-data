using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class PowersController : ControllerBase
{
    readonly ILogger<PowersController> _logger;
    readonly PowerService _service;
    readonly IHttpContextAccessor _contextAccessor;

    public PowersController(ILogger<PowersController> logger, PowerService service, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _service = service;
        _contextAccessor = contextAccessor;
    }
    
    [HttpGet("charts/categories")]
    public async Task<ChartData<int>> GetPowersChart() => await _service.GetPowersChart();
}