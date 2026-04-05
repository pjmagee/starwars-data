using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class CategoriesController : ControllerBase
{
    readonly ILogger<CategoriesController> _logger;
    readonly RecordService _recordService;
    readonly IHttpContextAccessor _contextAccessor;

    public CategoriesController(ILogger<CategoriesController> logger, RecordService recordService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordService = recordService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<IEnumerable<string>> Get()
    {
        return await _recordService.GetCollectionNames(_contextAccessor.HttpContext!.RequestAborted);
    }

    // Allow categories that may contain slashes (e.g., template URLs)
    [HttpGet("{*category}")]
    public async Task<PagedResult> Get(string category, [FromQuery] QueryParams queryParams, [FromQuery] Continuity? continuity = null, [FromQuery] Universe? universe = null)
    {
        return await _recordService.GetCollectionResult(
            category,
            searchText: queryParams.Search,
            page: queryParams.Page,
            pageSize: queryParams.PageSize,
            continuity: continuity,
            universe: universe,
            token: _contextAccessor.HttpContext!.RequestAborted
        );
    }
}
