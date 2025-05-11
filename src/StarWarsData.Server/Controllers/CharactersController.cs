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
    readonly CharacterRelationsService _relationsService;
    readonly IHttpContextAccessor _contextAccessor;

    public CharactersController(
        ILogger<CharactersController> logger, 
        CharacterService characterService, 
        CharacterRelationsService relationsService,
        IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _characterService = characterService;
        _relationsService = relationsService;
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
    
    [HttpGet("search")]
    public async Task<ActionResult<List<CharacterSearchDto>>> Search([FromQuery] string? search)
    {
        var results = await _relationsService.FindCharactersAsync(search);
        return Ok(results);
    }

    [HttpGet("{id:int}/relations")]
    public async Task<ActionResult<CharacterRelationsDto>> GetCharacterRelations(int id)
    {
        var rel = await _relationsService.GetRelationsByIdAsync(id);
        if (rel == null) return NotFound();
        return Ok(rel);
    }
}