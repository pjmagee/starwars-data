using Microsoft.AspNetCore.Mvc;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Features.Costs;

/// <summary>
/// Public read-only endpoint for the /costs page. Serves a pre-aggregated rolling
/// window of OpenAI organisation spend that the Admin app syncs nightly into
/// <c>admin.spend_daily</c>. No admin/billing scope is ever exposed at runtime.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CostsController(OpenAiSpendQueryService spend) : ControllerBase
{
    public sealed record DailySpendDto(string Date, long Cents, IReadOnlyList<LineItemDto> LineItems);

    public sealed record LineItemDto(string Name, long Cents);

    public sealed record OpenAiSummaryDto(
        long TotalCentsLast30Days,
        long TotalCentsLast90Days,
        string Currency,
        DateTime? LastSyncedAt,
        IReadOnlyList<DailySpendDto> DailyLast30,
        IReadOnlyList<LineItemDto> ByLineItemLast30
    );

    [HttpGet("openai-summary")]
    public async Task<ActionResult<OpenAiSummaryDto>> OpenAiSummary(CancellationToken ct = default)
    {
        var days = await spend.GetRecentDaysAsync(90, ct);
        if (days.Count == 0)
        {
            // Tell intermediaries not to cache the empty state — when the daily sync first
            // populates the collection, callers should see it on their next refresh rather
            // than wait out a stale TTL.
            Response.Headers.CacheControl = "no-store";
            return new OpenAiSummaryDto(0, 0, "usd", null, [], []);
        }

        // Underlying data refreshes once daily (04:30 UTC sync). Cache aggressively but
        // keep it short enough that a manual sync trigger surfaces within a few minutes.
        Response.Headers.CacheControl = "public, max-age=120";

        var currency = days[^1].Currency;
        var lastSyncedAt = days.Max(d => d.SyncedAt);

        var cutoff30 = DateTime.UtcNow.Date.AddDays(-30).ToString("yyyy-MM-dd");
        var last30 = days.Where(d => string.CompareOrdinal(d.Date, cutoff30) >= 0).ToList();

        var totalLast30 = last30.Sum(d => d.TotalCents);
        var totalLast90 = days.Sum(d => d.TotalCents);

        var byLineItem = last30.SelectMany(d => d.LineItems).GroupBy(li => li.Name).Select(g => new LineItemDto(g.Key, g.Sum(li => li.Cents))).OrderByDescending(li => li.Cents).ToList();

        var daily = last30.Select(d => new DailySpendDto(d.Date, d.TotalCents, d.LineItems.Select(li => new LineItemDto(li.Name, li.Cents)).ToList())).ToList();

        return new OpenAiSummaryDto(totalLast30, totalLast90, currency, lastSyncedAt, daily, byLineItem);
    }
}
