using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

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
    public async Task<PagedChartData> GetBirthDeaths([FromQuery] QueryParams query)
    {
        return await _characterService.GetChartData(query.Page, query.PageSize);
    }
}