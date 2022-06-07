using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Data;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class CharactersController : ControllerBase
{
    private readonly ILogger<CharactersController> _logger;
    private readonly CharacterService _characterService;
    private readonly IHttpContextAccessor _contextAccessor;

    public CharactersController(ILogger<CharactersController> logger, CharacterService characterService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _characterService = characterService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet("charts/births-deaths")]
    public async Task<PagedChartData<int>> GetBirthDeaths([FromQuery] QueryParams query) => await _characterService.GetBirthAndDeathsByYear(query.Page, query.PageSize);

    [HttpGet("charts/lifespans")]
    public async Task<PagedChartData<double>> GetLifespans([FromQuery] QueryParams query) => await _characterService.GetLifeSpans(query.Page, query.PageSize);
}