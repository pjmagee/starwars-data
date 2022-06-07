using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class WarsController : ControllerBase
{
    private readonly ILogger<WarsController> _logger;
    private readonly WarService _service;
    private readonly IHttpContextAccessor _contextAccessor;

    public WarsController(ILogger<WarsController> logger, WarService service, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _service = service;
        _contextAccessor = contextAccessor;
    }
    
    [HttpGet("charts/duration")]
    public async Task<PagedChartData<double>> GetWarsByDuration([FromQuery] QueryParams query) => await _service.GetWarsByDuration(query.Page, query.PageSize);
}