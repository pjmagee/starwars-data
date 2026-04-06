using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Suggestions;

/// <summary>
/// Read-side service for sampling Ask page suggestions from the <c>suggestions.examples</c>
/// cache produced by <see cref="SuggestionGenerator"/>. Matches the Frontend's global
/// filter semantics: Continuity (Canon/Legends/Both) and Realm (Starwars/Real/Both).
/// </summary>
public sealed class SuggestionService(IMongoClient mongoClient, IOptions<SettingsOptions> options)
{
    readonly IMongoCollection<GeneratedSuggestion> _collection = mongoClient.GetDatabase(options.Value.DatabaseName).GetCollection<GeneratedSuggestion>(Collections.SuggestionsExamples);

    /// <summary>
    /// Sample up to <paramref name="count"/> suggestions using MongoDB <c>$sample</c>
    /// (uniform, without replacement). All filter params are optional:
    /// <list type="bullet">
    ///   <item><paramref name="mode"/> — restrict to one Ask visualization mode.</item>
    ///   <item><paramref name="continuity"/> — only Canon or only Legends prompts. Null = both.</item>
    ///   <item><paramref name="realm"/> — Starwars (in-universe) or Real (publication/meta). Null = both.</item>
    /// </list>
    /// </summary>
    public async Task<List<GeneratedSuggestion>> SampleAsync(int count, string? mode = null, Continuity? continuity = null, Realm? realm = null, CancellationToken ct = default)
    {
        var filters = new List<BsonDocument>();
        if (!string.IsNullOrWhiteSpace(mode))
            filters.Add(new BsonDocument("mode", mode));
        if (continuity is not null)
            filters.Add(new BsonDocument("continuity", continuity.Value.ToString()));
        if (realm is not null)
            filters.Add(new BsonDocument("realm", realm.Value.ToString()));

        var agg = _collection.Aggregate();
        if (filters.Count > 0)
        {
            var combined = filters.Count == 1 ? filters[0] : new BsonDocument("$and", new BsonArray(filters));
            agg = agg.AppendStage<GeneratedSuggestion>(new BsonDocument("$match", combined));
        }
        agg = agg.AppendStage<GeneratedSuggestion>(new BsonDocument("$sample", new BsonDocument("size", count)));
        return await agg.ToListAsync(ct);
    }

    public Task<long> CountAsync(CancellationToken ct = default) => _collection.CountDocumentsAsync(FilterDefinition<GeneratedSuggestion>.Empty, cancellationToken: ct);
}
