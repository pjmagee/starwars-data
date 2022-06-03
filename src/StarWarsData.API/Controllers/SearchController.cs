using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.API.Controllers;

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

    [HttpGet("{search}", Name = "GetSearch")]
    public async Task<PagedResult> GetSearch(string search, [FromQuery] Paging paging)
    {
        return await _recordsService.GetSearchResult(search, paging.Page, paging.PageSize, _contextAccessor.HttpContext!.RequestAborted);
    }
}