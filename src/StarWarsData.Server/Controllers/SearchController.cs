using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class SearchController : ControllerBase
{
    private readonly ILogger<SearchController> _logger;
    private readonly RecordsService _recordsService;
    private readonly IHttpContextAccessor _contextAccessor;

    public SearchController(ILogger<SearchController> logger, RecordsService recordsService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordsService = recordsService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<PagedResult> Get([FromQuery] QueryParams queryParams)
    {
        return await _recordsService.GetSearchResult(queryParams.Search, queryParams.Page, queryParams.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}