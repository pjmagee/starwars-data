using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class RelationshipsController(RelationshipGraphService graphService) : ControllerBase
{
    [HttpGet("graph/{id:int}")]
    public async Task<ActionResult<GraphResult>> GetGraph(
        int id,
        [FromQuery] string collection = "Character",
        [FromQuery] int maxDepth = 3,
        [FromQuery] string[]? upLabels = null,
        [FromQuery] string[]? downLabels = null,
        [FromQuery] string[]? peerLabels = null
    )
    {
        var labelConfig = new RelationshipLabels
        {
            UpLabels = upLabels?.ToList() ?? [],
            DownLabels = downLabels?.ToList() ?? [],
            PeerLabels = peerLabels?.ToList() ?? [],
        };

        var result = await graphService.GetRelationshipGraphAsync(id, collection, maxDepth, labelConfig);

        if (result.Nodes.Count == 0)
            return NotFound();

        return result;
    }

    [HttpGet("{id:int}/immediate")]
    public async Task<ActionResult<ImmediateRelationsDto>> GetImmediate(
        int id,
        [FromQuery] string collection = "Character"
    )
    {
        var result = await graphService.GetImmediateRelationsAsync(id, collection);
        return result;
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<EntitySearchDto>>> Search(
        [FromQuery] string q,
        [FromQuery] string collection = "Character",
        [FromQuery] string? continuity = null
    )
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Query parameter 'q' is required.");

        var cont = Enum.TryParse<Models.Entities.Continuity>(continuity, true, out var c) ? c : (Models.Entities.Continuity?)null;
        var results = await graphService.FindEntitiesAsync(q, collection, cont);
        return results;
    }
}
