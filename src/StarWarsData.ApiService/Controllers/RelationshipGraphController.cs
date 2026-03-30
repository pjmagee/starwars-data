using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

/// <summary>
/// Public read-only endpoints for the relationship graph.
/// Admin/builder endpoints are in StarWarsData.Admin.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RelationshipGraphController(RelationshipGraphService graphService) : ControllerBase
{
    [HttpGet("query/{pageId:int}")]
    public async Task<GraphResult> QueryGraph(
        int pageId,
        [FromQuery] string collection = "Character",
        [FromQuery] int maxDepth = 3)
        => await graphService.GetRelationshipGraphAsync(pageId, collection, maxDepth);

    [HttpGet("entity/{pageId:int}")]
    public async Task<EntityRelationsDto?> GetEntity(
        int pageId,
        [FromQuery] string collection = "Character")
        => await graphService.GetRelationsByIdAsync(pageId, collection);

    [HttpGet("search")]
    public async Task<List<EntitySearchDto>> Search(
        [FromQuery] string q,
        [FromQuery] string collection = "Character")
        => await graphService.FindEntitiesAsync(q, collection);
}
