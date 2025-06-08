using Microsoft.AspNetCore.Mvc;
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

    [HttpGet("{category}")]
    public async Task<PagedResult> Get(string category, [FromQuery] QueryParams queryParams)
    {
        return await _recordService.GetCollectionResult(category, searchText: queryParams.Search, queryParams.Page, queryParams.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}