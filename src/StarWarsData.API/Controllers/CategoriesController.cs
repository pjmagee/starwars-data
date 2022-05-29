using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.API.Controllers;

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
    public async Task<IEnumerable<string>> GetCategories()
    {
        return await _recordsService.GetCollections(_contextAccessor.HttpContext!.RequestAborted);
    }

    [HttpGet("{collection}")]
    public async Task<PagedResult> GetCollection(string collection, [FromQuery] Paging paging)
    {
        return await _recordsService.GetCollectionResult(collection, paging.Page, paging.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}