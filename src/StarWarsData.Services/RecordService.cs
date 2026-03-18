using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

namespace StarWarsData.Services;

public class RecordService
{
    readonly ILogger<RecordService> _logger;
    readonly InfoboxToEventsTransformer _recordToEventsTransformer;
    readonly SettingsOptions _settingsOptions;
    readonly IMongoClient _mongoClient;
    readonly string _pagesDbName;
    readonly string _timelineEventsDbName;
    readonly YearHelper _yearHelper;
    readonly IEmbeddingGenerator<string, Embedding<float>> _textEmbeddingGenerationService;

    public RecordService(
        ILogger<RecordService> logger,
        IOptions<SettingsOptions> settingsOptions,
        YearHelper yearHelper,
        IMongoClient mongoClient,
        IEmbeddingGenerator<string, Embedding<float>> textEmbeddingGenerationService,
        InfoboxToEventsTransformer recordToEventsTransformer
    )
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _mongoClient = mongoClient;
        _pagesDbName = _settingsOptions.PagesDb;
        _timelineEventsDbName = _settingsOptions.TimelineEventsDb;
        _yearHelper = yearHelper;
        _textEmbeddingGenerationService = textEmbeddingGenerationService;
        _recordToEventsTransformer = recordToEventsTransformer;
    }

    IMongoCollection<Page> PagesCollection =>
        _mongoClient.GetDatabase(_pagesDbName).GetCollection<Page>("Pages");

    /// <summary>
    /// Returns distinct sanitized template names from Pages that have infoboxes.
    /// </summary>
    public async Task<List<string>> GetCollectionNames(CancellationToken cancellationToken)
    {
        var pages = PagesCollection;
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("infobox", new BsonDocument("$ne", BsonNull.Value))),
            new BsonDocument("$group", new BsonDocument("_id", "$infobox.Template")),
        };
        var cursor = await pages.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        var names = cursor
            .Select(d => d["_id"].IsBsonNull ? null : d["_id"].AsString)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => SanitizeTemplateName(n!))
            .Where(n => n != "Unknown")
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        return names;
    }

    public async Task DeletePagesCollections(CancellationToken cancellationToken)
    {
        var pagesDb = _mongoClient.GetDatabase(_pagesDbName);
        var collections = await pagesDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = await collections.ToListAsync(cancellationToken);
        foreach (var collection in collectionList)
        {
            await pagesDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task DeleteTimelineCollections(CancellationToken cancellationToken)
    {
        var timelineDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var collections = await timelineDb.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionList = await collections.ToListAsync(cancellationToken);
        foreach (var collection in collectionList)
        {
            await timelineDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task<PagedResult> GetSearchResult(
        string query,
        int page = 1,
        int pageSize = 50,
        CancellationToken token = default
    )
    {
        var pages = PagesCollection;
        var filter = Builders<Page>.Filter.And(
            Builders<Page>.Filter.Ne(p => p.Infobox, null),
            Builders<Page>.Filter.Regex("title", new BsonRegularExpression(query, "i"))
        );

        var total = await pages.CountDocumentsAsync(filter, cancellationToken: token);
        var data = await pages
            .Find(filter)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        return new PagedResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = data.Select(PageToInfobox).ToList(),
        };
    }

    public async Task<PagedResult> GetCollectionResult(
        string collectionName,
        string? searchText = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken token = default
    )
    {
        var pages = PagesCollection;

        // Match pages whose sanitized template matches the collection name
        var templateFilter = BuildTemplateFilter(collectionName);
        var filter = searchText is null
            ? templateFilter
            : Builders<Page>.Filter.And(
                templateFilter,
                Builders<Page>.Filter.Regex("title", new BsonRegularExpression(searchText, "i"))
            );

        var total = await pages.CountDocumentsAsync(filter, cancellationToken: token);
        var data = await pages
            .Find(filter)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        return new PagedResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = data.Select(PageToInfobox).ToList(),
        };
    }

    public async Task CreateCategorizedTimelineEvents(CancellationToken token)
    {
        var timelineEventsDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var pages = PagesCollection;

        // Get all distinct templates
        var templateNames = await GetCollectionNames(token);

        const int batchSize = 1000;
        var parallelOptions = new ParallelOptions { CancellationToken = token };

        await Parallel.ForEachAsync(
            templateNames,
            parallelOptions,
            async (templateName, ct) =>
            {
                try
                {
                    _logger.LogInformation(
                        "Processing timeline events for template: {TemplateName}",
                        templateName
                    );

                    var timelineCollection = timelineEventsDb.GetCollection<TimelineEvent>(templateName);

                    // Clear existing data
                    await timelineCollection.DeleteManyAsync(
                        FilterDefinition<TimelineEvent>.Empty,
                        ct
                    );

                    // Create indexes
                    var indexKeysDefinition = Builders<TimelineEvent>
                        .IndexKeys.Ascending(x => x.Demarcation)
                        .Ascending(x => x.Year);
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(indexKeysDefinition),
                        cancellationToken: ct
                    );

                    var templateIndexKeysDefinition = Builders<TimelineEvent>.IndexKeys.Ascending(
                        x => x.TemplateUri
                    );
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(templateIndexKeysDefinition),
                        cancellationToken: ct
                    );

                    var cleanedTemplateIndexKeysDefinition =
                        Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Template);
                    await timelineCollection.Indexes.CreateOneAsync(
                        new CreateIndexModel<TimelineEvent>(cleanedTemplateIndexKeysDefinition),
                        cancellationToken: ct
                    );

                    // Query pages matching this template
                    var templateFilter = BuildTemplateFilter(templateName);
                    var cursor = pages.Find(templateFilter).ToCursor(ct);

                    var batch = new List<TimelineEvent>(batchSize);
                    var totalAdded = 0;

                    while (await cursor.MoveNextAsync(ct))
                    {
                        foreach (var page in cursor.Current)
                        {
                            var timelineEvents = _recordToEventsTransformer.Transform(page);
                            batch.AddRange(timelineEvents);

                            if (batch.Count >= batchSize)
                            {
                                if (batch.Count > 0)
                                {
                                    await timelineCollection.InsertManyAsync(
                                        batch,
                                        cancellationToken: ct
                                    );
                                    totalAdded += batch.Count;
                                    batch.Clear();
                                }
                            }
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await timelineCollection.InsertManyAsync(batch, cancellationToken: ct);
                        totalAdded += batch.Count;
                    }

                    if (totalAdded == 0)
                    {
                        _logger.LogInformation(
                            "No timeline events generated for template: {TemplateName}",
                            templateName
                        );
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Added {Count} timeline events for template: {TemplateName}",
                            totalAdded,
                            templateName
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing timeline events for template: {TemplateName}",
                        templateName
                    );
                }
            }
        );

        _logger.LogInformation("Categorized timeline events creation complete");
    }

    public async Task CreateVectorIndexesAsync(CancellationToken cancellationToken)
    {
        await CreateCosineVectorIndexAsync("Pages");
    }

    async Task CreateCosineVectorIndexAsync(
        string collectionName,
        string field = "embedding",
        string indexName = "vector_index",
        int dims = 3072
    )
    {
        var vectorDef = new BsonDocument
        {
            {
                "fields",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "type", "vector" },
                        { "path", "embedding" },
                        { "numDimensions", dims },
                        { "similarity", "cosine" },
                    },
                }
            },
        };

        var model = new CreateSearchIndexModel(
            name: indexName,
            definition: vectorDef
        );

        var collection = _mongoClient
            .GetDatabase(_pagesDbName)
            .GetCollection<BsonDocument>(collectionName);
        await collection.SearchIndexes.CreateOneAsync(model);

        Console.WriteLine(
            $"Created cosine vector index for '{collection.CollectionNamespace.CollectionName}.{field}'"
        );
    }

    public async Task DeleteVectorIndexesAsync(CancellationToken cancellationToken)
    {
        var collection = _mongoClient.GetDatabase(_pagesDbName).GetCollection<BsonDocument>("Pages");
        await collection.SearchIndexes.DropOneAsync("vector_index", cancellationToken);
    }

    public async Task DeleteOpenAiEmbeddingsAsync(CancellationToken token)
    {
        var collection = _mongoClient.GetDatabase(_pagesDbName).GetCollection<Page>("Pages");
        var update = Builders<Page>.Update.Unset(
            new StringFieldDefinition<Page>("embedding")
        );
        await collection.UpdateManyAsync(
            FilterDefinition<Page>.Empty,
            update,
            cancellationToken: token
        );
        _logger.LogInformation("Embeddings removed from Pages collection");
    }

    public async Task<List<string>> GetTimelineCategories(
        CancellationToken cancellationToken = default
    )
    {
        var timelineEventsDb = _mongoClient.GetDatabase(_timelineEventsDbName);
        var names = await timelineEventsDb.ListCollectionNamesAsync(
            cancellationToken: cancellationToken
        );
        List<string> results = await names.ToListAsync(cancellationToken);
        return results.OrderBy(x => x).ToList();
    }

    public Task ProcessEmbeddingsAsync(CancellationToken none)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Build a filter that matches Pages whose infobox.Template, when sanitized, equals the given name.
    /// Uses regex to match the template URL pattern ending with the collection name.
    /// </summary>
    static FilterDefinition<Page> BuildTemplateFilter(string sanitizedName)
    {
        // Templates are stored as URLs like "https://starwars.fandom.com/wiki/Template:Character"
        // We match the end of the template string after the last colon
        return Builders<Page>.Filter.Regex(
            "infobox.Template",
            new BsonRegularExpression($":{sanitizedName.Replace(" ", "[_ ]")}$", "i")
        );
    }

    /// <summary>
    /// Extracts a clean template name from a template URL.
    /// e.g. "https://starwars.fandom.com/wiki/Template:Character" → "Character"
    /// </summary>
    public static string SanitizeTemplateName(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Unknown";

        var working = template;
        var wikiIdx = working.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
        if (wikiIdx >= 0)
        {
            working = working[(wikiIdx + 6)..];
        }

        var lastColon = working.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < working.Length - 1)
        {
            working = working[(lastColon + 1)..];
        }

        working = working.Split('?', '#')[0];
        return string.IsNullOrWhiteSpace(working) ? "Unknown" : working.Trim();
    }

    /// <summary>
    /// Project a Page into an Infobox shape for backward compatibility with frontend APIs.
    /// </summary>
    static Infobox PageToInfobox(Page page)
    {
        return new Infobox
        {
            PageId = page.PageId,
            WikiUrl = page.WikiUrl,
            Template = page.Infobox?.Template ?? "Unknown",
            ImageUrl = page.Infobox?.ImageUrl,
            Data = page.Infobox?.Data ?? [],
            Continuity = page.Continuity,
            PageTitle = page.Title,
        };
    }
}
