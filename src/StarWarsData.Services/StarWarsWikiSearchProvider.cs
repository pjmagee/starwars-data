using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services;

/// <summary>
/// Agent Framework context provider that exposes an on-demand wiki search tool.
/// Uses MongoDB regex text search against the Pages collection.
/// When <c>TextSearchProvider</c> ships in a future Microsoft.Agents.AI release,
/// this class can be replaced by it with a <c>searchAsync</c> delegate.
/// </summary>
public sealed class StarWarsWikiSearchProvider : MessageAIContextProvider
{
    private readonly IMongoCollection<BsonDocument> _pages;
    private readonly ILogger? _logger;
    private readonly AITool[] _tools;

    public StarWarsWikiSearchProvider(
        IMongoCollection<BsonDocument> pagesCollection,
        ILoggerFactory? loggerFactory = null
    )
    {
        _pages = pagesCollection;
        _logger = loggerFactory?.CreateLogger<StarWarsWikiSearchProvider>();

        _tools =
        [
            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => SearchAsync(query, ct),
                "search_wiki",
                "Search Star Wars wiki pages for background context, lore, and article text. "
                    + "Use when the user asks about history, events, explanations, or lore that goes beyond structured infobox data."
            ),
        ];
    }

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        // OnDemand mode: return no extra messages, just expose the tool
        return new(Enumerable.Empty<ChatMessage>());
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        return new(new AIContext { Tools = _tools });
    }

    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        _logger?.LogInformation("Wiki search: {Query}", query);

        var projection = Builders<BsonDocument>
            .Projection.Include("title")
            .Include("wikiUrl")
            .Include("content")
            .Include("infobox.Template")
            .Include("infobox.Data");

        // Use $text search (searches title + content via text index, ranked by relevance)
        var textFilter = Builders<BsonDocument>.Filter.Text(query);
        var textProjection = projection.MetaTextScore("score");
        var docs = await _pages
            .Find(textFilter)
            .Project(textProjection)
            .Sort(Builders<BsonDocument>.Sort.MetaTextScore("score"))
            .Limit(5)
            .ToListAsync(ct);

        // Fallback to regex on title if text search returns nothing
        // (handles exact phrase matches the text index may tokenize differently)
        if (docs.Count == 0)
        {
            _logger?.LogInformation("Text search returned no results, falling back to regex for: {Query}", query);
            var regexFilter = Builders<BsonDocument>.Filter.Regex(
                "title",
                new BsonRegularExpression(Regex.Escape(query), "i")
            );
            docs = await _pages.Find(regexFilter).Project(projection).Limit(5).ToListAsync(ct);
        }

        if (docs.Count == 0)
        {
            _logger?.LogInformation("Wiki search returned no results for: {Query}", query);
            return "No wiki pages found for that query.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Star Wars Wiki Context");
        sb.AppendLine("Use this information when answering. Cite the source page when relevant.");
        sb.AppendLine();

        foreach (var doc in docs)
        {
            var title = doc.GetValue("title", "").AsString;
            var wikiUrl = doc.GetValue("wikiUrl", "").AsString;
            var content = doc.Contains("content") ? doc["content"].AsString : "";

            sb.AppendLine($"### {title}");
            if (!string.IsNullOrEmpty(wikiUrl))
                sb.AppendLine($"Source: {wikiUrl}");

            // Include infobox summary if present
            if (doc.Contains("infobox") && doc["infobox"].IsBsonDocument)
            {
                var infobox = doc["infobox"].AsBsonDocument;
                if (infobox.Contains("Data") && infobox["Data"].IsBsonArray)
                {
                    var data = infobox["Data"].AsBsonArray;
                    var props = new List<string>();
                    foreach (var item in data.Take(10))
                    {
                        if (!item.IsBsonDocument)
                            continue;
                        var label = item.AsBsonDocument.GetValue("Label", "").AsString;
                        var values =
                            item.AsBsonDocument.Contains("Values") && item["Values"].IsBsonArray
                                ? string.Join(
                                    ", ",
                                    item["Values"].AsBsonArray.Select(v => v.AsString)
                                )
                                : "";
                        var links =
                            item.AsBsonDocument.Contains("Links") && item["Links"].IsBsonArray
                                ? string.Join(
                                    ", ",
                                    item["Links"]
                                        .AsBsonArray.Where(l => l.IsBsonDocument)
                                        .Select(l =>
                                            l.AsBsonDocument.GetValue("Content", "").AsString
                                        )
                                )
                                : "";
                        var value =
                            !string.IsNullOrEmpty(values) ? values
                            : !string.IsNullOrEmpty(links) ? links
                            : "";
                        if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value))
                            props.Add($"- {label}: {value}");
                    }
                    if (props.Count > 0)
                    {
                        sb.AppendLine("**Key facts:**");
                        foreach (var p in props)
                            sb.AppendLine(p);
                    }
                }
            }

            // Include article excerpt (first ~500 chars)
            if (!string.IsNullOrEmpty(content))
            {
                var excerpt = content.Length > 500 ? content[..500] + "…" : content;
                sb.AppendLine();
                sb.AppendLine(excerpt);
            }

            sb.AppendLine();
        }

        _logger?.LogInformation(
            "Wiki search returned {Count} results for: {Query}",
            docs.Count,
            query
        );
        return sb.ToString();
    }
}
