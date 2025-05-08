using MongoDB.Driver;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services.Data;
using StarWarsData.Models;

#pragma warning disable SKEXP0001

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class GalaxyMapController : ControllerBase
{
    private readonly ILogger<GalaxyMapController> _logger;
    private readonly MapService _mapService;

    public GalaxyMapController(ILogger<GalaxyMapController> logger, MapService mapService)
    {
        _logger = logger;
        _mapService = mapService;
    }

    [HttpGet("sectors")]
    public async Task<IEnumerable<GalaxyMapItem>> GetSectors()
    {
        // TODO: (how will we paint or style the 2D grid to identify sectors?)
        throw new NotImplementedException();
    }

    [HttpGet("regions")]
    public async Task<IEnumerable<GalaxyMapItem>> GetRegions()
    {
        // TODO: (how will we paint or style the 2D grid to identify regions?)
        throw new NotImplementedException();
    }

    [HttpGet("planets")]
    public async Task<IEnumerable<GalaxyMapItem>> GetPlanetsAsync()
    {
        return await _mapService.GetPlanets();
    }

    [HttpGet("grid/{letter}/{number}")]
    public async Task<IEnumerable<GalaxyMapItem>> GetGalaxyMapItemsAsync(string letter, int number)
    {
        var all = await _mapService.GetPlanets();
        return all.Where(p => p.Letter == letter && p.Number == number);
    }

    [HttpGet("grid")]
    public async Task<IEnumerable<GalaxyGridCell>> GetGalaxyGridCells()
    {
        return await _mapService.GetGalaxyGridCells();
    }
}
