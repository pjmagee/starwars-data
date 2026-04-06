using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

/// <summary>
/// Sole HTTP surface for the Galaxy Map feature (Explore + Timeline modes).
/// All routes are RESTful, kebab-case, plural-noun, and share <c>?continuity=</c> as a cross-cutting filter.
/// </summary>
[ApiController]
[Route("api/galaxy-map")]
[Produces("application/json")]
public class GalaxyMapUnifiedController(GalaxyMapReadService readService, MapService mapService) : ControllerBase
{
    // ── Overview ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level galaxy map summary: factions, eras, available years.
    /// Reads the single overview document from <c>galaxy.years</c>.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["continuity"])]
    public async Task<ActionResult<GalaxyOverviewDocument>> GetOverview([FromQuery] Continuity? continuity = null, CancellationToken ct = default)
    {
        var overview = await readService.GetOverviewAsync(continuity, ct);
        if (overview is null)
            return NotFound("Galaxy map not built yet. Run the ETL first.");
        return Ok(overview);
    }

    // ── Years (Timeline mode) ────────────────────────────────────────────────

    /// <summary>
    /// List year snapshots, optionally filtered to a <c>from</c>–<c>to</c> range.
    /// When both are omitted the full set is returned.
    /// </summary>
    [HttpGet("years")]
    [ResponseCache(Duration = 300)]
    public async Task<ActionResult<List<GalaxyYearDocument>>> GetYears([FromQuery] int? from = null, [FromQuery] int? to = null, CancellationToken ct = default)
    {
        var fromYear = from ?? int.MinValue;
        var toYear = to ?? int.MaxValue;
        var docs = await readService.GetYearRangeAsync(fromYear, toYear, ct);
        return Ok(docs);
    }

    /// <summary>Single per-year snapshot (territory control, event markers, governments).</summary>
    [HttpGet("years/{year:int}")]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<GalaxyYearDocument>> GetYear(int year, CancellationToken ct)
    {
        var doc = await readService.GetYearAsync(year, ct);
        if (doc is null)
            return NotFound($"No data for year {year}");
        return Ok(doc);
    }

    // ── Factions ─────────────────────────────────────────────────────────────

    /// <summary>Faction metadata map keyed by faction name (colour, icon, wiki URL).</summary>
    [HttpGet("factions")]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<Dictionary<string, TerritoryFactionInfo>>> GetFactions(CancellationToken ct)
    {
        var factions = await readService.GetFactionInfoAsync(ct);
        return Ok(factions);
    }

    // ── Geography (Explore mode) ─────────────────────────────────────────────

    /// <summary>Static galaxy geography: regions, sectors, trade routes, nebulas, grid bounds.</summary>
    [HttpGet("geography")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["continuity"])]
    public async Task<ActionResult<GalaxyGeography>> GetGeography([FromQuery] Continuity? continuity = null, CancellationToken ct = default)
    {
        var data = await mapService.GetGeographyAsync(continuity);
        return Ok(data);
    }

    /// <summary>Systems within a viewport bounding box, lazy-loaded as the user pans/zooms.</summary>
    [HttpGet("systems")]
    [ResponseCache(Duration = 120, VaryByQueryKeys = ["continuity"])]
    public async Task<ActionResult<GalaxyGeographySystems>> GetSystems(
        [FromQuery] int minCol,
        [FromQuery] int maxCol,
        [FromQuery] int minRow,
        [FromQuery] int maxRow,
        [FromQuery] Continuity? continuity = null,
        CancellationToken ct = default
    )
    {
        var data = await mapService.GetSystemsInRangeAsync(minCol, maxCol, minRow, maxRow, continuity);
        return Ok(data);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Unified galaxy search. Defaults to keyword match against grid-locatable pages; pass
    /// <c>semantic=true</c> to run an embedding search over article chunks and project hits back onto the grid.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<MapSearchResult>>> Search([FromQuery] string q, [FromQuery] bool semantic = false, [FromQuery] Continuity? continuity = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Query 'q' is required.");
        var minLength = semantic ? 3 : 2;
        if (q.Length < minLength)
            return BadRequest($"Query must be at least {minLength} characters.");

        var results = semantic ? await mapService.SemanticSearchGridAsync(q, continuity) : await mapService.SearchGridAsync(q, continuity);
        return Ok(results);
    }
}
