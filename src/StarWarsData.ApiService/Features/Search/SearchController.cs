using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

/// <summary>
/// Unified full-text search surface over the Star Wars corpus.
/// Supports three retrieval strategies selected via <c>?mode=</c>:
/// <list type="bullet">
///   <item><c>keyword</c> — MongoDB <c>$text</c> over <c>raw.pages</c>.</item>
///   <item><c>semantic</c> — <c>$vectorSearch</c> over <c>search.chunks</c> using OpenAI embeddings (default).</item>
///   <item><c>hybrid</c> — parallel keyword + semantic, score-merged by page.</item>
/// </list>
/// Semantic and hybrid modes are rate-limited per client; keyword is free.
/// </summary>
[ApiController]
[Route("api/search")]
[Produces("application/json")]
public class SearchController(SemanticSearchService semanticSearch, KeywordSearchService keywordSearch, SearchRateLimiter rateLimiter, UserSettingsService userSettings) : ControllerBase
{
    /// <summary>
    /// Search the corpus. Returns up to <paramref name="limit"/> ranked hits.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] string mode = "semantic",
        [FromQuery] string? type = null,
        [FromQuery] Continuity? continuity = null,
        [FromQuery] Realm? realm = null,
        [FromQuery] int limit = 10,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query 'q' is required" });

        if (limit is < 1 or > 25)
            limit = 10;

        if (mode is not ("keyword" or "semantic" or "hybrid"))
            mode = "semantic";

        // Keyword search is free (local index); semantic/hybrid consume the embedding API.
        if (mode is "semantic" or "hybrid")
        {
            var rateLimitResult = await CheckRateLimit();
            if (rateLimitResult is not null)
                return rateLimitResult;
        }

        string[]? types = string.IsNullOrWhiteSpace(type) ? null : [type];

        var results = mode switch
        {
            "keyword" => await keywordSearch.SearchAsync(q, types, continuity, realm, limit),
            "hybrid" => await HybridSearchAsync(q, types, continuity, realm, limit),
            _ => await semanticSearch.SearchAsync(q, types, continuity, realm, limit),
        };

        return Ok(
            new
            {
                query = q,
                mode,
                count = results.Count,
                results = results.Select(r => new
                {
                    r.PageId,
                    r.Title,
                    r.Heading,
                    r.Section,
                    r.WikiUrl,
                    r.Type,
                    r.Continuity,
                    r.Score,
                    Snippet = r.Text.Length > 300 ? r.Text[..300] + "…" : r.Text,
                    SectionUrl = r.SectionUrl,
                }),
            }
        );
    }

    async Task<List<SearchHit>> HybridSearchAsync(string query, string[]? types, Continuity? continuity, Realm? realm, int limit)
    {
        var keywordTask = keywordSearch.SearchAsync(query, types, continuity, realm, limit);
        var semanticTask = semanticSearch.SearchAsync(query, types, continuity, realm, limit);

        await Task.WhenAll(keywordTask, semanticTask);

        var keywordResults = keywordTask.Result;
        var semanticResults = semanticTask.Result;

        // Merge by PageId: semantic hits win on snippet quality, overlap boosts score.
        var merged = new Dictionary<int, SearchHit>();

        foreach (var r in semanticResults)
            merged[r.PageId] = r;

        foreach (var r in keywordResults)
        {
            if (merged.TryGetValue(r.PageId, out var existing))
                existing.Score = Math.Min(1.0, (existing.Score + r.Score) / 2 * 1.2);
            else
                merged[r.PageId] = r;
        }

        return merged.Values.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    async Task<IActionResult?> CheckRateLimit()
    {
        var userId = Request.Headers["X-User-Id"].FirstOrDefault();
        var isAuthenticated = !string.IsNullOrEmpty(userId);
        var hasByok = false;

        if (isAuthenticated)
            hasByok = await userSettings.HasOpenAiKeyAsync(userId!);

        var isAdmin = Request.Headers["X-User-Roles"].FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries).Contains("admin", StringComparer.OrdinalIgnoreCase) ?? false;

        if (hasByok || isAdmin)
            return null;

        var clientIp = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var clientId = userId ?? $"anon:{clientIp}";
        var result = rateLimiter.TryAcquire(clientId, isAuthenticated);

        if (!result.Allowed)
        {
            Response.Headers["Retry-After"] = ((int)(result.RetryAfter?.TotalSeconds ?? 1800)).ToString();
            return StatusCode(
                429,
                new
                {
                    error = "Rate limit exceeded",
                    limit = result.Limit,
                    isAuthenticated,
                    retryAfterSeconds = (int)(result.RetryAfter?.TotalSeconds ?? 1800),
                }
            );
        }

        return null;
    }
}
