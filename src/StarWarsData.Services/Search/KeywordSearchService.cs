using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Keyword search over raw wiki pages using MongoDB $text index.
/// Returns results in the same shape as <see cref="SearchHit"/> for unified display.
/// </summary>
public class KeywordSearchService
{
    readonly IMongoCollection<BsonDocument> _pages;
    readonly ILogger<KeywordSearchService> _logger;

    public KeywordSearchService(IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<KeywordSearchService> logger)
    {
        _pages = mongoClient.GetDatabase(settings.Value.DatabaseName).GetCollection<BsonDocument>(Collections.Pages);
        _logger = logger;
    }

    public async Task<List<SearchHit>> SearchAsync(string query, string[]? types = null, Continuity? continuity = null, Realm? realm = null, int limit = 10)
    {
        _logger.LogInformation(
            "KeywordSearch: query={Query}, types={Types}, continuity={Continuity}, realm={Realm}",
            query,
            types is not null ? string.Join(",", types) : "all",
            continuity?.ToString() ?? "all",
            realm?.ToString() ?? "all"
        );

        var filters = new List<FilterDefinition<BsonDocument>> { Builders<BsonDocument>.Filter.Text(query), Builders<BsonDocument>.Filter.Ne(PageBsonFields.Infobox, BsonNull.Value) };

        if (types is { Length: > 0 })
        {
            var typeFilter = types.Length == 1 ? Builders<BsonDocument>.Filter.Eq("infobox.Template", types[0]) : Builders<BsonDocument>.Filter.In("infobox.Template", types);
            filters.Add(typeFilter);
        }

        if (continuity.HasValue)
            filters.Add(Builders<BsonDocument>.Filter.Eq(PageBsonFields.Continuity, continuity.Value.ToString()));

        if (realm.HasValue)
            filters.Add(Builders<BsonDocument>.Filter.Eq(PageBsonFields.Realm, realm.Value.ToString()));

        var filter = Builders<BsonDocument>.Filter.And(filters);

        var projection = Builders<BsonDocument>
            .Projection.Include(PageBsonFields.PageId)
            .Include(PageBsonFields.Title)
            .Include(PageBsonFields.WikiUrl)
            .Include("content")
            .Include(PageBsonFields.Continuity)
            .Include(PageBsonFields.Realm)
            .Include("infobox.Template")
            .MetaTextScore("score");

        var docs = await _pages.Find(filter).Project(projection).Sort(Builders<BsonDocument>.Sort.MetaTextScore("score")).Limit(limit).ToListAsync();

        _logger.LogInformation("KeywordSearch: {Count} results returned", docs.Count);

        if (docs.Count == 0)
            return [];

        // Normalize text scores to 0-1 range for consistent display
        var maxScore = docs.Max(d => d.Contains("score") ? d["score"].AsDouble : 0);

        return docs.Select(d =>
            {
                var content = d.Contains("content") && !d["content"].IsBsonNull ? d["content"].AsString : "";
                var snippet = content.Length > 300 ? content[..300] + "…" : content;
                var rawScore = d.Contains("score") ? d["score"].AsDouble : 0;

                return new SearchHit
                {
                    PageId = d[PageBsonFields.PageId].AsInt32,
                    Title = d[PageBsonFields.Title].AsString,
                    Heading = "",
                    Section = "",
                    WikiUrl = d.Contains(PageBsonFields.WikiUrl) && !d[PageBsonFields.WikiUrl].IsBsonNull ? d[PageBsonFields.WikiUrl].AsString : "",
                    Type =
                        d.Contains(PageBsonFields.Infobox) && d[PageBsonFields.Infobox].IsBsonDocument && d[PageBsonFields.Infobox].AsBsonDocument.Contains(InfoboxBsonFields.Template)
                            ? d[PageBsonFields.Infobox][InfoboxBsonFields.Template].AsString
                            : "",
                    Continuity = d.Contains(PageBsonFields.Continuity) && !d[PageBsonFields.Continuity].IsBsonNull ? d[PageBsonFields.Continuity].AsString : "",
                    Realm = d.Contains(PageBsonFields.Realm) && !d[PageBsonFields.Realm].IsBsonNull ? d[PageBsonFields.Realm].AsString : "",
                    Text = snippet,
                    Score = maxScore > 0 ? rawScore / maxScore : 0,
                };
            })
            .ToList();
    }
}
