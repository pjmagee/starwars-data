using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/galaxy-map")]
[Produces("application/json")]
public class GalaxyMapUnifiedController(
    GalaxyMapReadService readService,
    MapService mapService) : ControllerBase
{
    // ── Pre-computed temporal data (from galaxy.years collection) ──

    [HttpGet("overview")]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<GalaxyOverviewDocument>> GetOverview(CancellationToken ct)
    {
        var overview = await readService.GetOverviewAsync(ct);
        if (overview is null) return NotFound("Galaxy map not built yet. Run the ETL first.");
        return Ok(overview);
    }

    [HttpGet("year/{year:int}")]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<GalaxyYearDocument>> GetYear(int year, CancellationToken ct)
    {
        var doc = await readService.GetYearAsync(year, ct);
        if (doc is null) return NotFound($"No data for year {year}");
        return Ok(doc);
    }

    [HttpGet("year-range")]
    [ResponseCache(Duration = 300)]
    public async Task<ActionResult<List<GalaxyYearDocument>>> GetYearRange(
        [FromQuery] int from, [FromQuery] int to, CancellationToken ct)
    {
        var docs = await readService.GetYearRangeAsync(from, to, ct);
        return Ok(docs);
    }

    [HttpGet("factions")]
    [ResponseCache(Duration = 600)]
    public async Task<ActionResult<Dictionary<string, TerritoryFactionInfo>>> GetFactions(CancellationToken ct)
    {
        var factions = await readService.GetFactionInfoAsync(ct);
        return Ok(factions);
    }

    // ── Static geographic data (delegates to existing MapService) ──

    [HttpGet("geography")]
    [ResponseCache(Duration = 300)]
    public async Task<ActionResult<GalaxyMapV2Overview>> GetGeography(
        [FromQuery] Continuity? continuity = null, CancellationToken ct = default)
    {
        var data = await mapService.GetGalaxyMapV2OverviewAsync(continuity);
        return Ok(data);
    }

    [HttpGet("systems")]
    [ResponseCache(Duration = 120)]
    public async Task<ActionResult<GalaxyMapV2Systems>> GetSystems(
        [FromQuery] int minCol, [FromQuery] int maxCol,
        [FromQuery] int minRow, [FromQuery] int maxRow,
        [FromQuery] Continuity? continuity = null, CancellationToken ct = default)
    {
        var data = await mapService.GetSystemsInRangeAsync(minCol, maxCol, minRow, maxRow, continuity);
        return Ok(data);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<MapSearchResult>>> Search(
        [FromQuery] string term, [FromQuery] Continuity? continuity = null)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
            return BadRequest("Search term must be at least 2 characters");
        var results = await mapService.SearchGridAsync(term, continuity);
        return Ok(results);
    }
}
