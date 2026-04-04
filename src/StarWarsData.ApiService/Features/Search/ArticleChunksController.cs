using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArticleChunksController(IMongoClient mongoClient, IOptions<SettingsOptions> settings)
    : ControllerBase
{
    readonly IMongoCollection<ArticleChunk> _chunks = mongoClient
        .GetDatabase(settings.Value.DatabaseName)
        .GetCollection<ArticleChunk>(Collections.SearchChunks);

    /// <summary>
    /// Get the intro section of an article by PageId (first chunk).
    /// </summary>
    [HttpGet("{pageId:int}/intro")]
    public async Task<ActionResult<ArticleIntroDto>> GetIntro(int pageId, CancellationToken ct)
    {
        var chunk = await _chunks
            .Find(c => c.PageId == pageId)
            .SortBy(c => c.ChunkIndex)
            .Limit(1)
            .Project(c => new ArticleIntroDto
            {
                Heading = c.Heading,
                Text = c.Text,
                WikiUrl = c.WikiUrl,
                Type = c.Type,
            })
            .FirstOrDefaultAsync(ct);

        if (chunk is null)
            return NotFound();

        return chunk;
    }
}

public class ArticleIntroDto
{
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
    public string WikiUrl { get; set; } = "";
    public string Type { get; set; } = "";
}
