using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly ILogger<CategoriesController> _logger;
    private readonly RecordsService _recordsService;
    private readonly IHttpContextAccessor _contextAccessor;

    public CategoriesController(ILogger<CategoriesController> logger, RecordsService recordsService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordsService = recordsService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<IEnumerable<string>> Get()
    {
        return await _recordsService.GetCollections(_contextAccessor.HttpContext!.RequestAborted);
    }

    [HttpGet("{category}")]
    public async Task<PagedResult> Get(string category, [FromQuery] QueryParams queryParams)
    {
        return await _recordsService.GetCollectionResult(category, searchText: queryParams.Search, queryParams.Page, queryParams.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}