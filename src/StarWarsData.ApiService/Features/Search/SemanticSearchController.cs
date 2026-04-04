using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SemanticSearchController(
    SemanticSearchService semanticSearch,
    KeywordSearchService keywordSearch,
    SearchRateLimiter rateLimiter,
    UserSettingsService userSettings
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string mode = "semantic",
        [FromQuery] string? type = null,
        [FromQuery] string? continuity = null,
        [FromQuery] string? universe = null,
        [FromQuery] int limit = 10,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "Query is required" });

        if (limit is < 1 or > 25)
            limit = 10;

        if (mode is not ("keyword" or "semantic" or "hybrid"))
            mode = "semantic";

        // Rate limiting — keyword search doesn't cost API calls, only semantic/hybrid do
        if (mode is "semantic" or "hybrid")
        {
            var rateLimitResult = await CheckRateLimit();
            if (rateLimitResult is not null)
                return rateLimitResult;
        }

        string[]? types = string.IsNullOrWhiteSpace(type) ? null : [type];
        Continuity? cont = Enum.TryParse<Continuity>(continuity, ignoreCase: true, out var c)
            ? c
            : null;
        Universe? uni = Enum.TryParse<Universe>(universe, ignoreCase: true, out var u) ? u : null;

        var results = mode switch
        {
            "keyword" => await keywordSearch.SearchAsync(query, types, cont, uni, limit),
            "hybrid" => await HybridSearchAsync(query, types, cont, uni, limit),
            _ => await semanticSearch.SearchAsync(query, types, cont, uni, limit),
        };

        return Ok(
            new
            {
                query,
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

    async Task<List<SemanticSearchResult>> HybridSearchAsync(
        string query,
        string[]? types,
        Continuity? continuity,
        Universe? universe,
        int limit
    )
    {
        var keywordTask = keywordSearch.SearchAsync(query, types, continuity, universe, limit);
        var semanticTask = semanticSearch.SearchAsync(query, types, continuity, universe, limit);

        await Task.WhenAll(keywordTask, semanticTask);

        var keywordResults = keywordTask.Result;
        var semanticResults = semanticTask.Result;

        // Merge: group by PageId, take the best score and prefer semantic snippet
        var merged = new Dictionary<int, SemanticSearchResult>();

        foreach (var r in semanticResults)
        {
            merged[r.PageId] = r;
        }

        foreach (var r in keywordResults)
        {
            if (merged.TryGetValue(r.PageId, out var existing))
            {
                // Boost score for items found by both strategies
                existing.Score = Math.Min(1.0, (existing.Score + r.Score) / 2 * 1.2);
            }
            else
            {
                merged[r.PageId] = r;
            }
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

        var isAdmin =
            Request
                .Headers["X-User-Roles"]
                .FirstOrDefault()
                ?.Split(',', StringSplitOptions.TrimEntries)
                .Contains("admin", StringComparer.OrdinalIgnoreCase) ?? false;

        if (hasByok || isAdmin)
            return null;

        var clientIp =
            Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        var clientId = userId ?? $"anon:{clientIp}";
        var result = rateLimiter.TryAcquire(clientId, isAuthenticated);

        if (!result.Allowed)
        {
            Response.Headers["Retry-After"] = (
                (int)(result.RetryAfter?.TotalSeconds ?? 1800)
            ).ToString();
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
