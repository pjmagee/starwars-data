using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Shared semantic vector search over article chunks.
/// Single implementation used by AI toolkits, map search, and any future consumer.
/// </summary>
public class SemanticSearchService
{
    readonly IMongoCollection<BsonDocument> _chunksRaw;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, IEmbeddingGenerator<string, Embedding<float>> embedder, ILogger<SemanticSearchService> logger)
    {
        _chunksRaw = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<BsonDocument>(Collections.SearchChunks);
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>
    /// Search article chunks by semantic similarity. Returns ranked results with page context.
    /// </summary>
    /// <param name="query">Natural language query to embed and search.</param>
    /// <param name="types">Optional entity type filter (e.g. "System", "CelestialBody"). Multiple types are OR'd.</param>
    /// <param name=ArticleChunkBsonFields.Continuity>Optional continuity filter.</param>
    /// <param name="realm">Optional realm filter.</param>
    /// <param name="limit">Max results to return.</param>
    /// <param name="minScore">Minimum cosine similarity score (0-1). Results below this are discarded.</param>
    public async Task<List<SearchHit>> SearchAsync(string query, string[]? types = null, Continuity? continuity = null, Realm? realm = null, int limit = 10, double minScore = 0.0)
    {
        _logger.LogInformation(
            "SemanticSearch: embedding query ({Length} chars), types={Types}, continuity={Continuity}, realm={Realm}",
            query.Length,
            types is not null ? string.Join(",", types) : "all",
            continuity?.ToString() ?? "all",
            realm?.ToString() ?? "all"
        );

        var embeddings = await _embedder.GenerateAsync([query]);
        var vector = embeddings[0].Vector.ToArray();

        _logger.LogInformation("SemanticSearch: embedding returned {Dims} dimensions, first3=[{V0:F4},{V1:F4},{V2:F4}]", vector.Length, vector[0], vector[1], vector[2]);

        var queryVector = new BsonArray(vector.Select(f => (double)f));
        var results = await SearchByVectorAsync(queryVector, types, continuity, realm, limit, minScore);

        _logger.LogInformation("SemanticSearch: {Count} results returned", results.Count);
        return results;
    }

    /// <summary>
    /// Search with a pre-computed embedding vector. Useful when the caller needs to reuse the same vector.
    /// </summary>
    public async Task<List<SearchHit>> SearchByVectorAsync(BsonArray queryVector, string[]? types = null, Continuity? continuity = null, Realm? realm = null, int limit = 10, double minScore = 0.0)
    {
        var searchDoc = new BsonDocument
        {
            { "index", "chunks_vector_index" },
            { "path", "embedding" },
            { "queryVector", queryVector },
            { "numCandidates", limit * 20 },
            { "limit", limit },
        };

        var filter = new BsonDocument();
        if (types is { Length: > 0 })
        {
            filter["type"] = types.Length == 1 ? (BsonValue)types[0] : new BsonDocument("$in", new BsonArray(types));
        }
        if (continuity.HasValue)
            filter[ArticleChunkBsonFields.Continuity] = continuity.Value.ToString();
        if (realm.HasValue)
            filter[ArticleChunkBsonFields.Realm] = realm.Value.ToString();
        if (filter.ElementCount > 0)
            searchDoc["filter"] = filter;

        var vectorSearchStage = new BsonDocument("$vectorSearch", searchDoc);

        var projectStage = new BsonDocument(
            "$project",
            new BsonDocument
            {
                { MongoFields.Id, 0 },
                { ArticleChunkBsonFields.PageId, 1 },
                { ArticleChunkBsonFields.Title, 1 },
                { "heading", 1 },
                { "section", 1 },
                { ArticleChunkBsonFields.WikiUrl, 1 },
                { "type", 1 },
                { ArticleChunkBsonFields.Continuity, 1 },
                { ArticleChunkBsonFields.Realm, 1 },
                { "text", 1 },
                { "score", new BsonDocument("$meta", "vectorSearchScore") },
            }
        );

        var docs = await _chunksRaw.Aggregate<BsonDocument>(new BsonDocument[] { vectorSearchStage, projectStage }).ToListAsync();

        _logger.LogInformation("SemanticSearch: $vectorSearch returned {Count} raw docs", docs.Count);

        return docs.Where(d => d.Contains("score") && d["score"].AsDouble >= minScore)
            .Select(d => new SearchHit
            {
                PageId = d[ArticleChunkBsonFields.PageId].AsInt32,
                Title = d[ArticleChunkBsonFields.Title].AsString,
                Heading = d.Contains("heading") && !d["heading"].IsBsonNull ? d["heading"].AsString : "",
                Section = d.Contains("section") && !d["section"].IsBsonNull ? d["section"].AsString : "",
                WikiUrl = d.Contains(ArticleChunkBsonFields.WikiUrl) && !d[ArticleChunkBsonFields.WikiUrl].IsBsonNull ? d[ArticleChunkBsonFields.WikiUrl].AsString : "",
                Type = d.Contains("type") && !d["type"].IsBsonNull ? d["type"].AsString : "",
                Continuity = d.Contains(ArticleChunkBsonFields.Continuity) && !d[ArticleChunkBsonFields.Continuity].IsBsonNull ? d[ArticleChunkBsonFields.Continuity].AsString : "",
                Realm = d.Contains(ArticleChunkBsonFields.Realm) && !d[ArticleChunkBsonFields.Realm].IsBsonNull ? d[ArticleChunkBsonFields.Realm].AsString : "",
                Text = d["text"].AsString,
                Score = d.Contains("score") ? d["score"].AsDouble : 0,
            })
            .ToList();
    }
}
