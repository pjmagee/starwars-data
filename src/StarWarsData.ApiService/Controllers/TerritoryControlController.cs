using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TerritoryControlController(TerritoryControlService territoryService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<TerritoryOverview> GetOverview(CancellationToken ct)
        => await territoryService.GetOverviewAsync(ct);

    [HttpGet("year/{year:int}")]
    public async Task<TerritoryYearResponse> GetYear(int year, CancellationToken ct)
        => await territoryService.GetYearAsync(year, ct);

    [HttpGet("factions")]
    public async Task<Dictionary<string, TerritoryControlService.FactionInfo>> GetFactions(CancellationToken ct)
        => await territoryService.GetFactionInfoAsync(ct);
}
