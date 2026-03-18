using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class PagesController : ControllerBase
{
    readonly RecordService _recordService;

    public PagesController(RecordService recordService)
    {
        _recordService = recordService;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Page>> GetById(int id, CancellationToken cancellationToken)
    {
        var page = await _recordService.GetPageById(id, cancellationToken);
        if (page is null) return NotFound();
        return page;
    }

    [HttpPost("batch")]
    public async Task<List<Page>> GetByIds([FromBody] int[] ids, CancellationToken cancellationToken)
    {
        return await _recordService.GetPagesByIds(ids, cancellationToken);
    }
}
