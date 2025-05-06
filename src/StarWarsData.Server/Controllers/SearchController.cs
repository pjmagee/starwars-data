using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Queries;
using StarWarsData.Services.Mongo;

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class SearchController : ControllerBase
{
    readonly ILogger<SearchController> _logger;
    readonly RecordService _recordService;
    readonly IHttpContextAccessor _contextAccessor;

    public SearchController(ILogger<SearchController> logger, RecordService recordService, IHttpContextAccessor contextAccessor)
    {
        _logger = logger;
        _recordService = recordService;
        _contextAccessor = contextAccessor;
    }

    [HttpGet]
    public async Task<PagedResult> Get([FromQuery] QueryParams queryParams)
    {
        return await _recordService.GetSearchResult(queryParams.Search, queryParams.Page, queryParams.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}