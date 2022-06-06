using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;


[ApiController]
[Route("[controller]")]
public class CharactersController : ControllerBase
{
    private readonly ILogger<CharactersController> _logger;
    private readonly CharactersService _charactersService;
    private readonly IHttpContextAccessor _contextAccessor;

    public CharactersController(ILogger<CharactersController> logger, CharactersService charactersService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _charactersService = charactersService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet("charts/births-deaths")]
    public async Task<PagedChartData> GetBirthDeaths([FromQuery] QueryParams query)
    {
        return await _charactersService.GetChartData(query.Page, query.PageSize);
    }
}