using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Stats;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Features.Stats;

/// <summary>
/// Design-037 / ADR-009: public, read-only corpus-health surface for the Frontend
/// "Miscellaneous" section. No auth (same class as <c>/api/costs</c>), no mutation,
/// continuity-agnostic by design. All figures come from <see cref="CorpusStatsService"/>'s
/// 5-minute cached snapshot; responses carry a short <c>Cache-Control</c> so
/// intermediaries don't re-ask within a refresh window.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StatsController(CorpusStatsService stats) : ControllerBase
{
    [HttpGet("corpus")]
    public async Task<ActionResult<CorpusStatsDto>> Corpus(CancellationToken ct = default)
    {
        var snapshot = await stats.GetSnapshotAsync(ct);
        Response.Headers.CacheControl = "public, max-age=120";
        return snapshot.Corpus;
    }

    [HttpGet("recent/pages")]
    public async Task<ActionResult<IReadOnlyList<RecentArticleDto>>> RecentPages([FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var snapshot = await stats.GetSnapshotAsync(ct);
        Response.Headers.CacheControl = "public, max-age=120";
        return snapshot.RecentPages.Take(Math.Clamp(limit, 1, 25)).ToList();
    }

    [HttpGet("recent/chunks")]
    public async Task<ActionResult<IReadOnlyList<RecentChunkDto>>> RecentChunks([FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var snapshot = await stats.GetSnapshotAsync(ct);
        Response.Headers.CacheControl = "public, max-age=120";
        return snapshot.RecentChunks.Take(Math.Clamp(limit, 1, 25)).ToList();
    }

    // ── Phase 2: read-only paginated browse (uncached; bounded + indexed) ────────

    [HttpGet("pages")]
    public async Task<ActionResult<StatsPage<RecentArticleDto>>> Pages([FromQuery] int skip = 0, [FromQuery] int take = 25, CancellationToken ct = default) =>
        await stats.BrowsePagesAsync(skip, take, ct);

    [HttpGet("chunks")]
    public async Task<ActionResult<StatsPage<RecentChunkDto>>> Chunks([FromQuery] int skip = 0, [FromQuery] int take = 25, CancellationToken ct = default) =>
        await stats.BrowseChunksAsync(skip, take, ct);
}
