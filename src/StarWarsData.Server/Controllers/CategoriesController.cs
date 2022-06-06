using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly ILogger<CategoriesController> _logger;
    private readonly RecordService _recordService;
    private readonly IHttpContextAccessor _contextAccessor;

    public CategoriesController(ILogger<CategoriesController> logger, RecordService recordService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordService = recordService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<IEnumerable<string>> Get()
    {
        return await _recordService.GetCollections(_contextAccessor.HttpContext!.RequestAborted);
    }

    [HttpGet("{category}")]
    public async Task<PagedResult> Get(string category, [FromQuery] QueryParams queryParams)
    {
        return await _recordService.GetCollectionResult(category, searchText: queryParams.Search, queryParams.Page, queryParams.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}