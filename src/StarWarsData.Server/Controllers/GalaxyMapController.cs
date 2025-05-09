using MongoDB.Driver;
using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services.Data;
using StarWarsData.Models;
using StarWarsData.Models.Queries;

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
    public async Task<ActionResult<IEnumerable<SectorDto>>> GetSectorsAsync()
    {
        try
        {
            var sectors = await _mapService.GetSectorsAsync();
            return Ok(sectors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sectors");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("sectors/{id:int}")]
    public async Task<ActionResult<SectorDto>> GetSectorAsync(int id)
    {
        try
        {
            var sector = await _mapService.GetSectorAsync(id);
            if (sector == null) return NotFound();
            return Ok(sector);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sector {SectorId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("regions")]
    public async Task<ActionResult<IEnumerable<RegionDto>>> GetRegionsAsync()
    {
        try
        {
            var regions = await _mapService.GetRegionsAsync();
            return Ok(regions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching regions");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("regions/{id:int}")]
    public async Task<ActionResult<RegionDto>> GetRegionAsync(int id)
    {
        try
        {
            var region = await _mapService.GetRegionAsync(id);
            if (region == null) return NotFound();
            return Ok(region);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching region {RegionId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("system/{id:int}")]
    public async Task<ActionResult<SystemDetailsDto>> GetSystemDetailsAsync(int id)
    {
        try
        {
            var system = await _mapService.GetSystemDetailsAsync(id);
            if (system == null) return NotFound();
            return Ok(system);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching system details for {SystemId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("celestialbody/{id:int}")]
    public async Task<ActionResult<CelestialBodyDetailsDto>> GetCelestialBodyDetailsAsync(int id)
    {
        try
        {
            var body = await _mapService.GetCelestialBodyDetailsAsync(id);
            if (body == null) return NotFound();
            return Ok(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching celestial body details for {BodyId}", id);
            return StatusCode(500, "Internal server error");
        }
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

    [HttpGet("regions/{regionId:int}/sectors")]
    public async Task<ActionResult<IEnumerable<SectorDto>>> GetSectorsByRegion(int regionId)
    {
        try
        {
            var sectors = await _mapService.GetSectorsByRegionAsync(regionId);
            return Ok(sectors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sectors for region {RegionId}", regionId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("sectors/{sectorId:int}/systems")]
    public async Task<ActionResult<IEnumerable<SystemDto>>> GetSystemsBySector(int sectorId)
    {
        try
        {
            var systems = await _mapService.GetSystemsBySectorAsync(sectorId);
            return Ok(systems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching systems for sector {SectorId}", sectorId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("systems/{systemId:int}/planets")]
    public async Task<ActionResult<IEnumerable<PlanetDto>>> GetPlanetsBySystem(int systemId)
    {
        try
        {
            var planets = await _mapService.GetPlanetsBySystemAsync(systemId);
            return Ok(planets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching planets for system {SystemId}", systemId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("regions/{regionId:int}/planets")]
    public async Task<ActionResult<IEnumerable<PlanetDto>>> GetOrphanPlanetsInRegion(int regionId)
    {
        try
        {
            var planets = await _mapService.GetOrphanPlanetsInRegionAsync(regionId);
            return Ok(planets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching orphan planets for region {RegionId}", regionId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("sectors/{sectorId:int}/planets")]
    public async Task<ActionResult<IEnumerable<PlanetDto>>> GetOrphanPlanetsInSector(int sectorId)
    {
        try
        {
            var planets = await _mapService.GetOrphanPlanetsInSectorAsync(sectorId);
            return Ok(planets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching orphan planets for sector {SectorId}", sectorId);
            return StatusCode(500, "Internal server error");
        }
    }
}
