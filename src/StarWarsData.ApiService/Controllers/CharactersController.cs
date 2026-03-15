using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class CharactersController : ControllerBase
{
    readonly ILogger<CharactersController> _logger;
    readonly CharacterRelationsService _relationsService;

    public CharactersController(
        ILogger<CharactersController> logger,
        CharacterRelationsService relationsService
    )
    {
        _logger = logger;
        _relationsService = relationsService;
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<CharacterSearchDto>>> Search([FromQuery] string search)
    {
        var results = await _relationsService.FindCharactersAsync(search);
        return Ok(results);
    }

    [HttpGet("{id:int}/relations")]
    public async Task<ActionResult<CharacterRelationsDto>> GetCharacterRelations(int id)
    {
        var rel = await _relationsService.GetRelationsByIdAsync(id);
        if (rel == null)
            return NotFound();
        return Ok(rel);
    }

    /// <summary>
    /// Fetch entire family graph (one round-trip)
    /// </summary>
    [HttpGet("{id:int}/family")]
    public async Task<ActionResult<FamilyGraphDto>> GetFamily(int id)
    {
        var graph = await _relationsService.GetFamilyGraphAsync(id);
        return Ok(graph);
    }

    /// <summary>
    /// Fetch immediate family (parents, partners, siblings, children) in one call
    /// </summary>
    [HttpGet("{id:int}/immediate")]
    public async Task<ActionResult<ImmediateFamilyDto>> GetImmediateFamily(int id)
    {
        var family = await _relationsService.GetImmediateFamilyAsync(id);
        return Ok(family);
    }

    /// <summary>
    /// Fetch multi-generation family tree up to maxDepth hops from the root.
    /// </summary>
    [HttpGet("{id:int}/tree")]
    public async Task<ActionResult<FamilyTreeResult>> GetFamilyTree(int id, [FromQuery] int maxDepth = 3)
    {
        var tree = await _relationsService.GetFamilyTreeAsync(id, maxDepth);
        return Ok(tree);
    }
}
