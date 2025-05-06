using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class CharactersController : ControllerBase
{
    readonly ILogger<CharactersController> _logger;
    readonly CharacterService _characterService;
    readonly IHttpContextAccessor _contextAccessor;

    public CharactersController(ILogger<CharactersController> logger, CharacterService characterService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _characterService = characterService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet("charts/births-deaths")]
    public async Task<PagedChartData<int>> GetBirthDeaths([FromQuery] QueryParams query)
    {
        return await _characterService.GetBirthAndDeathsByYear(query.Page, query.PageSize);
    }

    [HttpGet("charts/lifespans")]
    public async Task<PagedChartData<double>> GetLifespans([FromQuery] QueryParams query)
    {
        return await _characterService.GetLifeSpans(query.Page, query.PageSize);
    }
}