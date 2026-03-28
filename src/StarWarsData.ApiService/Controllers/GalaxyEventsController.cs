using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GalaxyEventsController(GalaxyEventsService eventsService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<GalaxyEventsOverview> GetOverview(
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Universe? universe = null,
        CancellationToken ct = default)
    {
        return await eventsService.GetOverviewAsync(continuity, universe, ct);
    }

    [HttpGet("layer")]
    public async Task<GalaxyEventLayer> GetLayer(
        [FromQuery] string lens = "Battle",
        [FromQuery] int yearStart = -100,
        [FromQuery] int yearEnd = 50,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Universe? universe = null,
        CancellationToken ct = default)
    {
        return await eventsService.GetEventLayerAsync(lens, yearStart, yearEnd, continuity, universe, ct);
    }

    [HttpGet("density")]
    public async Task<LensDensity> GetDensity(
        [FromQuery] string lens = "Battle",
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Universe? universe = null,
        CancellationToken ct = default)
    {
        return await eventsService.GetLensDensityAsync(lens, continuity, universe, ct);
    }

    [HttpGet("events")]
    public async Task<GalaxyEventList> GetEvents(
        [FromQuery] string lens = "Battle",
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Universe? universe = null,
        CancellationToken ct = default)
    {
        if (pageSize > 100) pageSize = 100;
        return await eventsService.GetEventListAsync(lens, search, page, pageSize, continuity, universe, ct);
    }
}
