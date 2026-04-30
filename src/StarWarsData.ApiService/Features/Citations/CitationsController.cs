using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.AI.Citations;

namespace StarWarsData.ApiService.Features.Citations;

/// <summary>
/// Resolves a list of KG node ids into <see cref="CitationReference"/> records,
/// each carrying the surfaces a user can navigate to (Wiki, GraphExplorer,
/// GalaxyMap, …). Drives the <c>CitationCard.razor</c> UI on the copilot
/// sidebar and the Knowledge Graph page. See Design-030.
/// </summary>
[ApiController]
[Route("api/citations")]
[Produces("application/json")]
public class CitationsController(ICitationResolver resolver) : ControllerBase
{
    public sealed record ResolveRequest(int[] PageIds);

    [HttpPost("resolve")]
    public async Task<ActionResult<IReadOnlyList<CitationReference>>> Resolve([FromBody] ResolveRequest body, CancellationToken ct)
    {
        if (body.PageIds is null || body.PageIds.Length == 0)
            return Ok(Array.Empty<CitationReference>());

        // Cap the request size at 100 — the citation surface lives in chat
        // bubbles and inline KG rows; if a caller is asking to resolve more
        // than that, something went wrong upstream.
        if (body.PageIds.Length > 100)
            return BadRequest("Too many ids; max 100 per request.");

        var refs = await resolver.ResolveAsync(body.PageIds, ct);
        return Ok(refs);
    }
}
