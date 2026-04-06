using Microsoft.AspNetCore.Mvc;
using StarWarsData.Models.Entities;
using StarWarsData.Services.Suggestions;

namespace StarWarsData.ApiService.Features.Suggestions;

/// <summary>
/// Serves dynamically generated Ask page example questions sampled from the
/// <c>suggestions.examples</c> cache. The generator refreshes the cache via a
/// Hangfire recurring job in the Admin app. Supports the Frontend's global
/// filters — Continuity (Canon/Legends) and Realm (Star Wars/Real world).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SuggestionsController(SuggestionService service) : ControllerBase
{
    public sealed record SuggestionDto(string Mode, string Shape, string Prompt, string Continuity, string Realm, IReadOnlyList<int> EntityIds);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SuggestionDto>>> Sample(
        [FromQuery] int count = 6,
        [FromQuery] string? mode = null,
        [FromQuery] string? continuity = null,
        [FromQuery] string? realm = null,
        CancellationToken ct = default
    )
    {
        if (count <= 0 || count > 50)
            count = 6;

        Continuity? c = Enum.TryParse<Continuity>(continuity, ignoreCase: true, out var pc) ? pc : null;
        Realm? r = Enum.TryParse<Realm>(realm, ignoreCase: true, out var pr) ? pr : null;

        List<Models.Entities.GeneratedSuggestion> items;
        if (r is null)
        {
            // Both realms enabled: balance the sample so real-world prompts aren't crowded
            // out by the much larger Starwars pool. Split the requested count and draw
            // from each realm independently, then interleave.
            var half = Math.Max(1, count / 2);
            var otherHalf = count - half;
            var starwars = await service.SampleAsync(half, mode, c, Realm.Starwars, ct);
            var real = await service.SampleAsync(otherHalf, mode, c, Realm.Real, ct);

            // If either side is empty, backfill with the other so we still return `count` items.
            if (real.Count < otherHalf)
                starwars.AddRange(await service.SampleAsync(otherHalf - real.Count, mode, c, Realm.Starwars, ct));
            if (starwars.Count < half)
                real.AddRange(await service.SampleAsync(half - starwars.Count, mode, c, Realm.Real, ct));

            items = Interleave(starwars, real).Take(count).ToList();
        }
        else
        {
            items = await service.SampleAsync(count, mode, c, r, ct);
        }

        return items.Select(s => new SuggestionDto(s.Mode, s.Shape, s.Prompt, s.Continuity.ToString(), s.Realm.ToString(), s.EntityIds)).ToList();
    }

    static IEnumerable<T> Interleave<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var max = Math.Max(a.Count, b.Count);
        for (int i = 0; i < max; i++)
        {
            if (i < a.Count)
                yield return a[i];
            if (i < b.Count)
                yield return b[i];
        }
    }
}
